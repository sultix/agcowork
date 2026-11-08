import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import { RenderNode, renderMarkdown } from './comment-markdown';

/**
 * Time (ms) one comment sidebar may spend on formatting comments. Each comment is cheap (the
 * limits of `isPathological` keep the worst case below ~50 ms), but a record shows up to 500
 * threads: past this budget, comments not formatted yet are shown as plain text.
 */
export const COMMENT_RENDER_BUDGET_MS = new InjectionToken<number>('COMMENT_RENDER_BUDGET_MS', {
  providedIn: 'root',
  factory: () => 150,
});

/** Clock for the budget (ms); replaced in tests. */
export const COMMENT_RENDER_CLOCK = new InjectionToken<() => number>('COMMENT_RENDER_CLOCK', {
  providedIn: 'root',
  factory: () => () => performance.now(),
});

/** Most rendered comments kept (by text); the oldest are dropped first. */
const CACHE_LIMIT = 1_000;

/**
 * Formats the comments of one sidebar within a time budget ({@link COMMENT_RENDER_BUDGET_MS}).
 * Every formatting and first DOM build is measured and charged; once the budget is spent, new
 * texts render as plain text. Results are kept by text, so a live reload, a re-created thread or
 * a collapsed and reopened thread costs nothing and changes nothing; the budget is not renewed
 * by reloads, only by {@link reset} (another record). Provided by the comment sidebar.
 */
@Injectable()
export class CommentRenderer {
  private readonly budget = inject(COMMENT_RENDER_BUDGET_MS);
  private readonly clock = inject(COMMENT_RENDER_CLOCK);
  private readonly cache = new Map<string, RenderNode[]>();
  private spentMs = 0;

  /** The budget is spent: comments formatted from now on are plain text. */
  readonly exhausted = signal(false);

  /** The tree of `markdown`: from the cache, formatted while the budget lasts, else plain. */
  render(markdown: string): RenderNode[] {
    const cached = this.cache.get(markdown);
    if (cached) {
      return cached;
    }
    let nodes: RenderNode[];
    if (this.exhausted()) {
      nodes = [{ kind: 'plain', text: markdown }];
    } else {
      const started = this.clock();
      nodes = renderMarkdown(markdown);
      this.charge(this.clock() - started);
    }
    if (this.cache.size >= CACHE_LIMIT) {
      this.cache.delete(this.cache.keys().next().value as string);
    }
    this.cache.set(markdown, nodes);
    return nodes;
  }

  /** Runs `build` (DOM work for a new tree) and charges its time. */
  measure(build: () => void): void {
    const started = this.clock();
    build();
    this.charge(this.clock() - started);
  }

  /** Starts over (another record). */
  reset(): void {
    this.cache.clear();
    this.spentMs = 0;
    this.exhausted.set(false);
  }

  private charge(ms: number): void {
    this.spentMs += Math.max(0, ms);
    if (this.spentMs >= this.budget && !this.exhausted()) {
      this.exhausted.set(true);
    }
  }
}
