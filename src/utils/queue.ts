export class AsyncQueue {
  private current: Promise<unknown> = Promise.resolve();

  run<T>(task: () => Promise<T>): Promise<T> {
    const next = this.current.then(task, task);
    this.current = next.catch(() => undefined);
    return next;
  }

  async acquire(): Promise<() => void> {
    let releaseLock: (() => void) | undefined;
    const lock = new Promise<void>((resolve) => {
      releaseLock = resolve;
    });
    const previous = this.current;
    this.current = previous.then(() => lock, () => lock);
    await previous;

    let released = false;
    return () => {
      if (!released) {
        released = true;
        releaseLock?.();
      }
    };
  }
}
