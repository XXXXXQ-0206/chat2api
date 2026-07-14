import { Command } from "commander";
import { loadConfig } from "./config.js";
import { createLogger } from "./logger.js";
import { createProvider } from "./provider/provider.js";
import { ContextProbe } from "./probe/contextProbe.js";
import { createServer } from "./server.js";
import type { DeepSeekMode } from "./types.js";

const program = new Command();

program
  .name("chat2api")
  .description("Local DeepSeek web chat API bridge.")
  .version("0.1.0");

program
  .command("serve", { isDefault: true })
  .option("--host <host>", "host to bind")
  .option("--port <port>", "port to bind")
  .option("--provider <provider>", "browser or mock")
  .option("--offline", "disable every browser-provider operation")
  .action(async (options: { host?: string; port?: string; provider?: "browser" | "mock"; offline?: boolean }) => {
    const config = loadConfig({
      host: options.host,
      port: options.port ? Number(options.port) : undefined,
      provider: options.provider,
      offlineMode: options.offline
    });
    const { app, logger } = await createServer(config);
    await app.listen({ host: config.host, port: config.port });
    logger.info({ url: `http://${config.host}:${config.port}` }, "chat2api server started");
  });

program
  .command("login")
  .description("Open the DeepSeek login page and wait for manual login.")
  .option("--timeout-ms <ms>", "login wait timeout", "180000")
  .option("--offline", "disable every browser-provider operation")
  .action(async (options: { timeoutMs: string; offline?: boolean }) => {
    const config = loadConfig({ offlineMode: options.offline });
    const logger = createLogger(config);
    const provider = createProvider(config, logger);
    await provider.beginLogin();
    const status = await provider.waitForLogin(Number(options.timeoutMs));
    logger.info(status, "login status");
    await provider.shutdown();
  });

program
  .command("auth-status")
  .description("Print current DeepSeek login status.")
  .option("--offline", "disable every browser-provider operation")
  .action(async (options: { offline?: boolean }) => {
    const config = loadConfig({ offlineMode: options.offline });
    const logger = createLogger(config);
    const provider = createProvider(config, logger);
    const status = await provider.authStatus();
    process.stdout.write(`${JSON.stringify(status, null, 2)}\n`);
    await provider.shutdown();
  });

program
  .command("probe-context")
  .description("Probe a mode context length and persist the measured limit.")
  .requiredOption("--mode <mode>", "expert or vision; fast is disabled")
  .option("--min-chars <n>", "minimum probe chars", "1024")
  .option("--max-chars <n>", "maximum probe chars", "200000")
  .option("--offline", "disable every browser-provider operation")
  .action(async (options: { mode: DeepSeekMode; minChars: string; maxChars: string; offline?: boolean }) => {
    const config = loadConfig({ offlineMode: options.offline });
    const logger = createLogger(config);
    const provider = createProvider(config, logger);
    const probe = new ContextProbe(config, provider, logger);
    const result = await probe.run({
      mode: options.mode,
      minChars: Number(options.minChars),
      maxChars: Number(options.maxChars)
    });
    process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
    await provider.shutdown();
  });

program.parseAsync(process.argv).catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.stack ?? error.message : String(error)}\n`);
  process.exitCode = 1;
});
