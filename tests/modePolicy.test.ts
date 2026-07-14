import { describe, expect, it } from "vitest";
import { resolveMode } from "../src/api/common.js";
import { AppError } from "../src/utils/errors.js";

describe("mode policy", () => {
  it("defaults requests without images to expert", () => {
    expect(resolveMode(undefined, undefined, false)).toBe("expert");
  });

  it("keeps image requests on vision when no mode is supplied", () => {
    expect(resolveMode(undefined, undefined, true)).toBe("vision");
  });

  it.each([
    ["deepseek-chat2api-fast", undefined],
    [undefined, "fast"]
  ])("rejects fast requests with a stable unsupported_mode error", (model, explicitMode) => {
    try {
      resolveMode(model, explicitMode, false);
      throw new Error("expected fast mode to be rejected");
    } catch (error) {
      expect(error).toBeInstanceOf(AppError);
      expect(error).toMatchObject({ statusCode: 400, code: "unsupported_mode" });
    }
  });
});
