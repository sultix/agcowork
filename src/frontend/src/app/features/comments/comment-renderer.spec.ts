import { TestBed } from '@angular/core/testing';
import {
  COMMENT_RENDER_BUDGET_MS,
  COMMENT_RENDER_CLOCK,
  CommentRenderer,
} from './comment-renderer';
import { isPathological } from './comment-markdown';

describe('CommentRenderer', () => {
  function create(providers: unknown[] = []): CommentRenderer {
    TestBed.configureTestingModule({ providers: [CommentRenderer, ...(providers as never[])] });
    return TestBed.inject(CommentRenderer);
  }

  it('render_BudgetSpent_FurtherNewTextsArePlain_KnownTextsStayFormatted', () => {
    let now = 0;
    const renderer = create([
      { provide: COMMENT_RENDER_BUDGET_MS, useValue: 100 },
      // every measurement takes 40 ms
      { provide: COMMENT_RENDER_CLOCK, useValue: () => (now += 40) },
    ]);

    expect(renderer.render('**eins**')[0].kind).toBe('paragraph');
    expect(renderer.render('**zwei**')[0].kind).toBe('paragraph');
    expect(renderer.exhausted()).toBe(false);
    expect(renderer.render('**drei**')[0].kind).toBe('paragraph'); // 120 ms: spent now
    expect(renderer.exhausted()).toBe(true);

    expect(renderer.render('**vier**')).toEqual([{ kind: 'plain', text: '**vier**' }]);
    // a live reload brings the same texts again: from the cache, unchanged, free
    const before = now;
    expect(renderer.render('**eins**')[0].kind).toBe('paragraph');
    expect(renderer.render('**vier**')[0].kind).toBe('plain');
    expect(now).toBe(before);

    renderer.reset(); // another record
    expect(renderer.exhausted()).toBe(false);
    expect(renderer.render('**vier**')[0].kind).toBe('paragraph');
  });

  it('render_DomBuildTime_IsChargedToo', () => {
    let now = 0;
    const renderer = create([
      { provide: COMMENT_RENDER_BUDGET_MS, useValue: 100 },
      { provide: COMMENT_RENDER_CLOCK, useValue: () => now },
    ]);

    renderer.measure(() => (now += 150));

    expect(renderer.exhausted()).toBe(true);
  });

  it('render_ManyCommentsJustBelowTheLimits_TotalTimeIsBounded', () => {
    const renderer = create();
    const row = '|' + 'c|'.repeat(6) + '\n';
    // 200 distinct comments, each formatted would take 5-20 ms: 1-4 s in total
    const comments = Array.from(
      { length: 200 },
      (_, i) => `| ${i} |` + 'a|'.repeat(5) + '\n|' + '-|'.repeat(6) + '\n' + row.repeat(270),
    );

    expect(comments.every((c) => !isPathological(c))).toBe(true);
    const started = performance.now();
    const kinds = comments.map((c) => renderer.render(c)[0].kind);
    const elapsed = performance.now() - started;

    expect(renderer.exhausted()).toBe(true);
    expect(kinds.filter((k) => k === 'table').length).toBeGreaterThan(0);
    expect(kinds.at(-1)).toBe('plain');
    // budget 150 ms plus at most one comment over it, plus the plain ones (microseconds each)
    expect(elapsed).toBeLessThan(400);
  });
});
