using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using Chat2ApiTray.Services.Api;
using Chat2ApiTray.Services;
using Microsoft.Data.Sqlite;

namespace Chat2ApiTray.Services.Context;

public interface IContextIndex
{
    Task UpsertAsync(string conversationId, List<ApiMessage> messages, CancellationToken cancellationToken);

    Task<List<ApiMessage>> SearchAsync(string conversationId, string query, int maxMessages, int tokenBudget, CancellationToken cancellationToken);
}

public sealed class SqliteVectorContextIndex : IContextIndex
{
    private const int Dimensions = 256;
    private const int MaxIndexedMessageTokens = 1024;

    private readonly string _databasePath;

    public SqliteVectorContextIndex(string databasePath)
    {
        _databasePath = databasePath;
        LocalDataDirectorySecurity.EnsurePrivateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
    }

    public async Task UpsertAsync(string conversationId, List<ApiMessage> messages, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        foreach (var sourceMessage in messages.Where(message => !string.IsNullOrWhiteSpace(message.Content)))
        {
            var segmentIndex = 0;
            foreach (var message in SplitForIndex(sourceMessage))
            {
                var id = StableId(conversationId, sourceMessage, segmentIndex);
                var vectorId = await UpsertMetadataAsync(connection, transaction, id, conversationId, message, cancellationToken);
                await ReplaceVectorAsync(connection, transaction, vectorId, Embed(message.Content), cancellationToken);
                segmentIndex += 1;
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<ApiMessage>> SearchAsync(string conversationId, string query, int maxMessages, int tokenBudget, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var queryTerms = Terms(query).ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select metadata.conversation_id, metadata.role, metadata.content, metadata.tokens, vectors.distance
            from context_vectors vectors
            join context_vector_metadata metadata on metadata.vector_id = vectors.rowid
            where vectors.embedding match $embedding
              and vectors.k = $candidateCount
            order by vectors.distance asc
            """;
        command.Parameters.AddWithValue("$candidateCount", Math.Max(maxMessages * 12, 64));
        AddEmbedding(command, "$embedding", Embed(query));

        var selected = new List<ApiMessage>();
        var usedTokens = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!string.Equals(conversationId, reader.GetString(0), StringComparison.Ordinal))
            {
                continue;
            }

            var content = reader.GetString(2);
            if (queryTerms.Count > 0 && !Terms(content).Any(queryTerms.Contains))
            {
                continue;
            }

            var tokens = reader.GetInt32(3);
            if (usedTokens + tokens > tokenBudget)
            {
                continue;
            }

            selected.Add(new ApiMessage(reader.GetString(1), content));
            usedTokens += tokens;
            if (selected.Count >= maxMessages)
            {
                break;
            }
        }

        return selected;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);
        LoadVectorExtension(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            create table if not exists context_vector_metadata(
                vector_id integer primary key,
                id text not null unique,
                conversation_id text not null,
                role text not null,
                content text not null,
                tokens integer not null,
                updated_at text not null
            );
            create index if not exists idx_context_vector_metadata_conversation on context_vector_metadata(conversation_id, updated_at);
            create virtual table if not exists context_vectors using vec0(embedding float[{Dimensions}]);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    public static void LoadVectorExtension(SqliteConnection connection)
    {
        var extension = OperatingSystem.IsWindows() ? "vec0.dll" : OperatingSystem.IsMacOS() ? "vec0.dylib" : "vec0.so";
        foreach (var path in VectorExtensionPaths(extension))
        {
            if (File.Exists(path))
            {
                connection.LoadExtension(path);
                return;
            }
        }

        connection.LoadVector();
    }

    private static IEnumerable<string> VectorExtensionPaths(string extension)
    {
        var runtime = OperatingSystem.IsWindows()
            ? $"win-{ArchitectureSuffix()}"
            : OperatingSystem.IsMacOS()
                ? $"osx-{ArchitectureSuffix()}"
                : $"linux-{ArchitectureSuffix()}";
        yield return Path.Combine(AppContext.BaseDirectory, "runtimes", runtime, "native", extension);
        yield return Path.Combine(AppContext.BaseDirectory, extension);

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is not string nativeSearchDirectories)
        {
            yield break;
        }

        foreach (var directory in nativeSearchDirectories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory, extension);
        }
    }

    private static string ArchitectureSuffix()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };
    }

    private static async Task<long> UpsertMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string conversationId,
        ApiMessage message,
        CancellationToken cancellationToken)
    {
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "select vector_id from context_vector_metadata where id = $id";
        lookup.Parameters.AddWithValue("$id", id);
        var existing = await lookup.ExecuteScalarAsync(cancellationToken);
        if (existing is long vectorId)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                update context_vector_metadata
                set role = $role, content = $content, tokens = $tokens, updated_at = $updatedAt
                where vector_id = $vectorId
                """;
            update.Parameters.AddWithValue("$role", message.Role);
            update.Parameters.AddWithValue("$content", message.Content);
            update.Parameters.AddWithValue("$tokens", TokenEstimator.Estimate(message.Content));
            update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$vectorId", vectorId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            return vectorId;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            insert into context_vector_metadata(id, conversation_id, role, content, tokens, updated_at)
            values ($id, $conversationId, $role, $content, $tokens, $updatedAt);
            select last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$conversationId", conversationId);
        insert.Parameters.AddWithValue("$role", message.Role);
        insert.Parameters.AddWithValue("$content", message.Content);
        insert.Parameters.AddWithValue("$tokens", TokenEstimator.Estimate(message.Content));
        insert.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ReplaceVectorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long vectorId,
        float[] embedding,
        CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "delete from context_vectors where rowid = $vectorId";
        delete.Parameters.AddWithValue("$vectorId", vectorId);
        await delete.ExecuteNonQueryAsync(cancellationToken);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "insert into context_vectors(rowid, embedding) values ($vectorId, $embedding)";
        insert.Parameters.AddWithValue("$vectorId", vectorId);
        AddEmbedding(insert, "$embedding", embedding);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddEmbedding(SqliteCommand command, string name, float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        command.Parameters.Add(name, SqliteType.Blob).Value = bytes;
    }

    private static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var term in Terms(text))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(term));
            var slot = BitConverter.ToUInt16(hash, 0) % Dimensions;
            vector[slot] += (hash[2] & 1) == 0 ? 1f : -1f;
        }

        var norm = MathF.Sqrt(vector.Sum(value => value * value));
        if (norm > 0)
        {
            for (var index = 0; index < vector.Length; index += 1)
            {
                vector[index] /= norm;
            }
        }

        return vector;
    }

    private static IEnumerable<string> Terms(string text)
    {
        return text.Split([' ', '\r', '\n', '\t', '.', ',', ':', ';', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Trim().ToLowerInvariant())
            .Where(term => term.Length >= 2);
    }

    private static IEnumerable<ApiMessage> SplitForIndex(ApiMessage message)
    {
        if (TokenEstimator.Estimate(message.Content) <= MaxIndexedMessageTokens)
        {
            yield return message;
            yield break;
        }

        var offset = 0;
        while (offset < message.Content.Length)
        {
            var length = Math.Min(message.Content.Length - offset, MaxIndexedMessageTokens * 4);
            while (length > 1 && TokenEstimator.Estimate(message.Content.Substring(offset, length)) > MaxIndexedMessageTokens)
            {
                length = Math.Max(1, length / 2);
            }

            yield return message with { Content = message.Content.Substring(offset, length) };
            offset += length;
        }
    }

    private static string StableId(string conversationId, ApiMessage sourceMessage, int segmentIndex)
    {
        var seed = $"{conversationId}\n{sourceMessage.Role}\n{sourceMessage.ToolCallId}\n{sourceMessage.Content}\n{segmentIndex}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash[..16]).ToLowerInvariant();
    }
}
