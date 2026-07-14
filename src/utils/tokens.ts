import type { UnifiedMessage } from "../types.js";

export function estimateTokens(text: string): number {
  let tokens = 0;
  for (const char of text) {
    const code = char.charCodeAt(0);
    if (/\s/.test(char)) continue;
    tokens += code > 0x2fff ? 1 : 0.25;
  }
  return Math.max(1, Math.ceil(tokens));
}

export function estimateMessagesTokens(messages: UnifiedMessage[]): number {
  return messages.reduce((sum, message) => sum + estimateTokens(`${message.role}: ${message.content}`) + 4, 0);
}

export function trimMessagesToTokenLimit(messages: UnifiedMessage[], limit: number): UnifiedMessage[] {
  const kept: UnifiedMessage[] = [];
  let total = 0;
  for (const message of [...messages].reverse()) {
    const cost = estimateMessagesTokens([message]);
    if (kept.length > 0 && total + cost > limit) break;
    kept.unshift(message);
    total += cost;
  }
  const system = messages.find((message) => message.role === "system");
  if (system && !kept.includes(system)) {
    kept.unshift(system);
  }
  return kept;
}
