import type { Logger } from "pino";
import { chromium, type BrowserContext, type Locator, type Page } from "playwright";
import type { AuthStatus, Chat2ApiConfig, DeepSeekMode, UploadedFileRef } from "../types.js";
import { AppError, toErrorMessage } from "../utils/errors.js";
import { AsyncQueue } from "../utils/queue.js";
import { SessionStore } from "./sessionStore.js";

interface SendOptions {
  prompt: string;
  mode: DeepSeekMode;
  thinking?: boolean;
  webSearch?: boolean;
  files?: UploadedFileRef[];
}

const selectors = {
  prompt: ["textarea", "[contenteditable='true']", "div[role='textbox']"],
  sendButton: [
    "button[type='submit']",
    "button:has-text('发送')",
    "button:has-text('Send')",
    "[aria-label*='发送']",
    "[aria-label*='Send']"
  ],
  assistantMessage: [
    "[data-testid*='assistant']",
    "[class*='assistant']",
    ".ds-markdown",
    ".markdown",
    "[class*='markdown']"
  ],
  login: ["text=登录", "text=Log in", "text=Sign in"],
  fileInput: ["input[type='file']"]
};

const modeLabels: Record<DeepSeekMode, string[]> = {
  fast: ["快速", "Fast", "普通", "chat"],
  expert: ["专家", "Expert", "深度思考", "reason"],
  vision: ["识图", "图像", "图片", "Vision", "image"]
};

export class DeepSeekWebClient {
  private context?: BrowserContext;
  private page?: Page;
  private readonly queue = new AsyncQueue();
  private readonly sessionStore: SessionStore;

  constructor(
    private readonly config: Chat2ApiConfig,
    private readonly logger: Logger
  ) {
    this.sessionStore = new SessionStore(config);
  }

  async beginLogin(): Promise<AuthStatus> {
    const page = await this.ensurePage();
    await page.goto(this.config.deepSeekUrl, { waitUntil: "domcontentloaded" });
    return this.authStatus("Login page opened. Complete login in the browser window.");
  }

  async waitForLogin(timeoutMs = 180000): Promise<AuthStatus> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
      const status = await this.authStatus();
      if (status.loggedIn) return status;
      await new Promise((resolve) => setTimeout(resolve, 1500));
    }
    throw new AppError(408, "login_timeout", "Timed out while waiting for DeepSeek login.");
  }

  async authStatus(message?: string): Promise<AuthStatus> {
    const page = this.getActivePage();
    const liveLoggedIn = page ? await this.isLoggedIn(page) : undefined;
    if (liveLoggedIn && this.context) {
      await this.sessionStore.save(this.context).catch((error) => {
        this.logger.warn({ error: toErrorMessage(error) }, "failed to save browser storage state");
      });
    }

    const savedAt = await this.sessionStore.readSavedAt();
    const expiresAt = savedAt
      ? new Date(savedAt.getTime() + this.config.sessionTtlMinutes * 60 * 1000)
      : undefined;
    const storedLoginIsFresh = expiresAt ? expiresAt.getTime() > Date.now() : false;
    const loggedIn = liveLoggedIn ?? storedLoginIsFresh;
    const needsLogin = !loggedIn || (expiresAt ? expiresAt.getTime() < Date.now() : false);
    const defaultMessage = needsLogin
      ? "DeepSeek login is missing or near expiry."
      : page
        ? undefined
        : "Stored DeepSeek session is within the configured TTL; live browser status was not checked.";

    return {
      loggedIn,
      needsLogin,
      loginUrl: this.config.deepSeekUrl,
      lastCheckedAt: new Date().toISOString(),
      lastLoginAt: savedAt?.toISOString(),
      expiresAt: expiresAt?.toISOString(),
      message: message ?? defaultMessage
    };
  }

  async send(options: SendOptions): Promise<string> {
    return this.queue.run(async () => {
      const page = await this.ensureReadyPage();
      await this.configureMode(page, options.mode, options.thinking, options.webSearch);
      await this.uploadFiles(page, options.files ?? []);
      const before = await this.extractAssistantText(page);
      await this.fillPrompt(page, options.prompt);
      await this.sendCurrentPrompt(page);
      return this.waitForResponse(page, before);
    });
  }

  async *stream(options: SendOptions): AsyncGenerator<string> {
    const release = await this.queue.acquire();
    try {
      const page = await this.ensureReadyPage();
      await this.configureMode(page, options.mode, options.thinking, options.webSearch);
      await this.uploadFiles(page, options.files ?? []);
      const before = await this.extractAssistantText(page);
      await this.fillPrompt(page, options.prompt);
      await this.sendCurrentPrompt(page);
      yield* this.waitForResponseStream(page, before);
    } finally {
      release();
    }
  }

  async probe(mode: DeepSeekMode, prompt: string, thinking?: boolean, webSearch?: boolean): Promise<boolean> {
    try {
      const response = await this.send({ mode, prompt, thinking, webSearch });
      return response.trim().length > 0;
    } catch (error) {
      this.logger.warn({ mode, error: toErrorMessage(error) }, "context probe attempt failed");
      return false;
    }
  }

  async close(): Promise<void> {
    await this.context?.close().catch(() => undefined);
    this.context = undefined;
    this.page = undefined;
  }

  private async ensureReadyPage(): Promise<Page> {
    const page = await this.ensurePage();
    if (!(await this.isLoggedIn(page))) {
      throw new AppError(401, "login_required", "DeepSeek login is required. Run `chat2api login` or POST /auth/login.");
    }
    return page;
  }

  private async ensurePage(): Promise<Page> {
    if (this.page && !this.page.isClosed()) return this.page;
    if (!this.context) {
      this.context = await this.launchBrowserContext();
      this.context.setDefaultTimeout(10000);
      this.context.setDefaultNavigationTimeout(30000);
    }
    this.page = this.context.pages()[0] ?? (await this.context.newPage());
    if (!this.page.url().startsWith(this.config.deepSeekUrl)) {
      await this.page.goto(this.config.deepSeekUrl, { waitUntil: "domcontentloaded" });
    }
    return this.page;
  }

  private getActivePage(): Page | undefined {
    if (this.page && !this.page.isClosed()) return this.page;
    return this.context?.pages().find((page) => !page.isClosed());
  }

  private async launchBrowserContext(): Promise<BrowserContext> {
    const channels = unique([
      this.config.browserChannel,
      undefined,
      "msedge",
      "chrome"
    ]);
    let lastError: unknown;

    for (const channel of channels) {
      const options = {
        headless: this.config.browserHeadless,
        viewport: null,
        acceptDownloads: true,
        ...(channel ? { channel } : {})
      };
      try {
        this.logger.info({ channel: channel ?? "playwright-chromium" }, "launching browser context");
        return await chromium.launchPersistentContext(this.config.browserProfileDir, options);
      } catch (error) {
        lastError = error;
        const message = toErrorMessage(error);
        this.logger.warn({ channel: channel ?? "playwright-chromium", error: message }, "browser launch attempt failed");
        if (!isMissingBrowserRuntime(message)) break;
      }
    }

    throw new AppError(
      503,
      "browser_launch_failed",
      `Unable to launch a browser for DeepSeek. Install Playwright Chromium with \`npx playwright install chromium\` or set CHAT2API_BROWSER_CHANNEL=msedge/chrome. Last error: ${toErrorMessage(lastError)}`
    );
  }

  private async isLoggedIn(page: Page): Promise<boolean> {
    try {
      await page.waitForLoadState("domcontentloaded", { timeout: 5000 }).catch(() => undefined);
      const prompt = await this.firstVisible(page, selectors.prompt, 1500);
      if (prompt) return true;
      const login = await this.firstVisible(page, selectors.login, 1000);
      return !login && !/login|signin|sign-in/i.test(page.url());
    } catch {
      return false;
    }
  }

  private async configureMode(page: Page, mode: DeepSeekMode, thinking?: boolean, webSearch?: boolean): Promise<void> {
    await this.clickAnyText(page, modeLabels[mode]).catch(() => undefined);
    if (thinking !== undefined) {
      await this.setToggle(page, ["深度思考", "Deep Think", "Reason"], thinking).catch(() => undefined);
    }
    if (webSearch !== undefined) {
      await this.setToggle(page, ["联网", "搜索", "Search", "Web"], webSearch).catch(() => undefined);
    }
  }

  private async setToggle(page: Page, labels: string[], desired: boolean): Promise<void> {
    for (const label of labels) {
      const locator = page.getByText(label, { exact: false }).first();
      if (!(await locator.isVisible().catch(() => false))) continue;
      const selected = await locator.getAttribute("aria-pressed").catch(() => null);
      const checked = await locator.getAttribute("aria-checked").catch(() => null);
      const active = selected === "true" || checked === "true";
      if (active !== desired) await locator.click();
      return;
    }
  }

  private async clickAnyText(page: Page, labels: string[]): Promise<void> {
    for (const label of labels) {
      const locator = page.getByText(label, { exact: false }).first();
      if (await locator.isVisible().catch(() => false)) {
        await locator.click();
        return;
      }
    }
  }

  private async uploadFiles(page: Page, files: UploadedFileRef[]): Promise<void> {
    if (files.length === 0) return;
    const input = await this.firstVisible(page, selectors.fileInput, 2000, false);
    if (!input) throw new AppError(400, "file_upload_unavailable", "DeepSeek file upload input was not found.");
    await input.setInputFiles(files.map((file) => file.path));
  }

  private async fillPrompt(page: Page, prompt: string): Promise<void> {
    const input = await this.firstVisible(page, selectors.prompt, 5000);
    if (!input) throw new AppError(503, "prompt_box_not_found", "DeepSeek prompt box was not found.");
    await input.click();
    await input.fill("").catch(async () => {
      await page.keyboard.press(process.platform === "darwin" ? "Meta+A" : "Control+A");
      await page.keyboard.press("Backspace");
    });
    await input.fill(prompt).catch(async () => {
      await page.keyboard.insertText(prompt);
    });
  }

  private async sendCurrentPrompt(page: Page): Promise<void> {
    const button = await this.firstVisible(page, selectors.sendButton, 2000);
    if (button) {
      await button.click();
      return;
    }
    await page.keyboard.press("Enter");
  }

  private async waitForResponse(page: Page, previous: string): Promise<string> {
    let content = "";
    for await (const delta of this.waitForResponseStream(page, previous)) {
      content += delta;
    }
    return content;
  }

  private async *waitForResponseStream(page: Page, previous: string): AsyncGenerator<string> {
    const started = Date.now();
    let emitted = "";
    let lastSnapshot = "";
    let stableCount = 0;
    while (Date.now() - started < this.config.completionTimeoutMs) {
      const current = await this.extractAssistantText(page);
      if (current && current !== previous) {
        if (current.startsWith(emitted)) {
          const delta = current.slice(emitted.length);
          if (delta) {
            emitted = current;
            yield delta;
          }
        }
        if (current === lastSnapshot) stableCount += 1;
        else stableCount = 0;
        lastSnapshot = current;
        if (stableCount >= 2) {
          if (current !== emitted) {
            this.logger.warn("assistant DOM changed non-monotonically while streaming; forwarding final snapshot");
            yield current;
          }
          return;
        }
      }
      await new Promise((resolve) => setTimeout(resolve, 1200));
    }
    throw new AppError(504, "completion_timeout", "Timed out waiting for DeepSeek response.");
  }

  private async extractAssistantText(page: Page): Promise<string> {
    for (const selector of selectors.assistantMessage) {
      const texts = await page.locator(selector).allTextContents().catch(() => []);
      const cleaned = texts.map((text) => text.trim()).filter(Boolean);
      if (cleaned.length > 0) return cleaned.at(-1) ?? "";
    }
    return page.locator("body").innerText({ timeout: 2000 }).catch(() => "");
  }

  private async firstVisible(page: Page, selectorList: string[], timeout: number, requireVisible = true): Promise<Locator | undefined> {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
      for (const selector of selectorList) {
        const locator = page.locator(selector).first();
        const ok = requireVisible ? await locator.isVisible().catch(() => false) : await locator.count().then((count) => count > 0).catch(() => false);
        if (ok) return locator;
      }
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    return undefined;
  }
}

function unique<T>(values: T[]): T[] {
  return values.filter((value, index) => values.findIndex((candidate) => candidate === value) === index);
}

function isMissingBrowserRuntime(message: string): boolean {
  return /executable doesn't exist|not found|install.*browser|browser.*was not found|Chromium distribution/i.test(message);
}
