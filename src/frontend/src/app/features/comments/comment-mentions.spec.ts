import { renderMarkdown } from './comment-markdown';
import { MAX_QUOTE_DEPTH, findMentions, mentionLink } from './comment-mentions';

// Mirrors tests/Agcowork.Domain.Tests/Comments/CommentMarkdownTests.cs: the chips of the web app
// follow the server's rules, so both must agree on every case.
const A = '0190f3a2-7c4e-7b1a-9d2e-3f4a5b6c7d8e';
const B = '0190f3a2-7c4e-7b1a-9d2e-000000000002';

const ids = (markdown: string) => findMentions(markdown).actorIds;

describe('findMentions', () => {
  it('findMentions_SeveralLinks_InOrder_DuplicatesOnce', () => {
    expect(
      ids(`[@A](actor:${A}) und [@B](actor:${B}), nochmal [@Anders genannt](actor:${A}).`),
    ).toEqual([A, B]);
  });

  it('findMentions_UuidAndSchemeInAnyCase_AngleBrackets_FreeLinkText', () => {
    expect(ids(`[@a](actor:${A.toUpperCase()})`)).toEqual([A]);
    expect(ids(`[jemand](ACTOR:${A})`)).toEqual([A]);
    expect(ids(`[@a](<actor:${A}>)`)).toEqual([A]);
    expect(ids(`[@a]( actor:${A} )`)).toEqual([A]);
    expect(mentionLink('Tech-Lead', A)).toBe(`[@Tech-Lead](actor:${A})`);
    expect(ids(mentionLink('Tech-Lead', A))).toEqual([A]);
  });

  it('findMentions_InCodeSpan_NotCounted', () => {
    expect(ids(`Syntax: \`[@a](actor:${A})\``)).toEqual([]);
    expect(ids(`Doppelt: \`\` x \`[@a](actor:${A})\` y \`\``)).toEqual([]);
    expect(ids(`\`[@a](actor:${A})\` aber [@b](actor:${B})`)).toEqual([B]);
    // An unclosed backtick is literal text, the link after it counts.
    expect(ids(`ein \` allein [@a](actor:${A})`)).toEqual([A]);
    // A code span may span lines of one paragraph.
    expect(ids(`\`code\n[@a](actor:${A})\``)).toEqual([]);
  });

  it('findMentions_InFencedOrIndentedCodeBlock_NotCounted', () => {
    expect(ids(`\`\`\`\n[@a](actor:${A})\n\`\`\``)).toEqual([]);
    expect(ids(`~~~markdown\n[@a](actor:${A})\n~~~`)).toEqual([]);
    expect(ids(`\`\`\`\`\n\`\`\`\n[@a](actor:${A})\n\`\`\`\n\`\`\`\``)).toEqual([]);
    expect(ids(`Text\n\n    [@a](actor:${A})\n`)).toEqual([]);
    expect(ids(`\`\`\`\n[@a](actor:${A})\n\`\`\`\n\n[@b](actor:${B})`)).toEqual([B]);
    // Unclosed fence: code until the end.
    expect(ids(`\`\`\`\n[@a](actor:${A})`)).toEqual([]);
  });

  it('findMentions_IndentedLines_InParagraphOrList_Counted', () => {
    expect(ids(`Absatz\n    [@a](actor:${A})`)).toEqual([A]);
    expect(ids(`- Punkt\n\n    [@a](actor:${A}) gehört zum Punkt`)).toEqual([A]);
    expect(ids(`1. [@a](actor:${A})\n   - [@b](actor:${B})`)).toEqual([A, B]);
  });

  it('findMentions_BrokenOrOtherLinks_Ignored_InvalidActorTargetsReported', () => {
    const scan = findMentions(
      `[@a] (actor:${A}) [@b](actor:${A} [c](https://x.y/actor:${A}) \\[@d](actor:${B}) [@e](actor:nicht-uuid) [@f](actor:)`,
    );

    expect(scan.actorIds).toEqual([]);
    expect(scan.invalidTargets).toEqual(['actor:nicht-uuid', 'actor:']);
  });

  it('findMentions_CodeSpanBindsBeforeLink', () => {
    expect(ids(`[a\`](actor:${A})\``)).toEqual([]);
    expect(ids(`[a](actor:\`${A})\``)).toEqual([]);
    expect(ids(`[\`@a\`](actor:${A})`)).toEqual([A]);
    expect(ids(`\\\`[@a](actor:${A})\``)).toEqual([A]);
  });

  it('findMentions_IndentedLineAfterHeadingOrFence_IsCode', () => {
    expect(ids(`# Titel\n    [@a](actor:${A})`)).toEqual([]);
    expect(ids(`\`\`\`\nx\n\`\`\`\n    [@a](actor:${A})`)).toEqual([]);
    expect(ids(`# Titel [@b](actor:${B})\n    [@a](actor:${A})`)).toEqual([B]);
  });

  it('findMentions_LinkTextAcrossLines_Counted', () => {
    expect(ids(`Hallo [@Tech\nLead](actor:${A})`)).toEqual([A]);
    expect(ids(`Hallo [@Tech\n\nLead](actor:${A})`)).toEqual([]);
  });

  it('findMentions_InHtmlComment_NotCounted', () => {
    expect(ids(`Text <!-- [@a](actor:${A}) --> Ende`)).toEqual([]);
    expect(ids(`<!--\n[@a](actor:${A})\n\n[@a](actor:${A})\n-->`)).toEqual([]);
    expect(ids(`<!-- weg -->\n\n[@b](actor:${B})`)).toEqual([B]);
    expect(ids(`<!--\n[@a](actor:${A})\n-->\n[@b](actor:${B})`)).toEqual([B]);
    expect(ids(`a <!-- b [@a](actor:${A})`)).toEqual([A]);
  });

  it('findMentions_DotNetWhitespaceSet_NotJavaScripts', () => {
    // U+0085 is whitespace for the server: a fence may start after it, a link target may be padded.
    expect(ids(`\u0085\`\`\`\n[@a](actor:${A})`)).toEqual([]);
    expect(ids(`[@a](\u0085actor:${A}\u2003)`)).toEqual([A]);
    // U+FEFF is no whitespace for the server (JavaScript's \\s would accept it).
    expect(ids(`[@a](\uFEFFactor:${A})`)).toEqual([]);
    expect(ids(`[@a](actor:${A}\uFEFF)`)).toEqual([]);
    expect(ids(`\uFEFF\`\`\`\n[@a](actor:${A})`)).toEqual([A]);
  });

  it('findMentions_EmptyOrNull_Nothing', () => {
    expect(ids('')).toEqual([]);
    expect(findMentions(null).actorIds).toEqual([]);
  });

  it('findMentions_CrLfLineEnds_SameAsLf', () => {
    expect(ids(`\`\`\`\r\n[@a](actor:${A})\r\n\`\`\`\r\n\r\n[@b](actor:${B})`)).toEqual([B]);
  });

  it('findMentions_InBlockQuote_Counted', () => {
    expect(ids(`> [@a](actor:${A})`)).toEqual([A]);
    expect(ids(`>[@a](actor:${A})`)).toEqual([A]);
    expect(ids(`   > Zitat\n> [@a](actor:${A})`)).toEqual([A]);
    expect(ids(`> [@a](actor:${A})\n\nDanach [@b](actor:${B})`)).toEqual([A, B]);
    expect(ids(`>> [@a](actor:${A})`)).toEqual([A]);
    expect(ids(`> > [@a](actor:${A})`)).toEqual([A]);
    expect(ids(`> [@a\n> b](actor:${A})`)).toEqual([A]);
    expect(ids(`> Zitat\n[@a](actor:${A}) gehört noch dazu`)).toEqual([A]);
    expect(ids(`> Zitat\n\n    [@b](actor:${B})\n> [@a](actor:${A})`)).toEqual([A]);
  });

  it('findMentions_CodeInBlockQuote_NotCounted', () => {
    expect(ids(`> \`\`\`\n> [@a](actor:${A})\n> \`\`\``)).toEqual([]);
    expect(ids(`> ~~~\n> [@a](actor:${A})`)).toEqual([]);
    expect(ids(`>\`\`\`\n>[@a](actor:${A})\n>\`\`\``)).toEqual([]);
    expect(ids(`> > \`\`\`\n> > [@a](actor:${A})`)).toEqual([]);
    expect(ids(`>     [@a](actor:${A})`)).toEqual([]);
    expect(ids(`> Zitat\n>\n>     [@a](actor:${A})`)).toEqual([]);
    expect(ids(`>\t\t[@a](actor:${A})`)).toEqual([]);
    expect(ids(`> >     [@a](actor:${A})`)).toEqual([]);
    expect(ids(`> <!--\n> [@a](actor:${A})\n> -->`)).toEqual([]);
    expect(ids(`> \`[@a](actor:${A})\``)).toEqual([]);
  });

  it('findMentions_BlockQuoteBoundaries_FollowCommonMark', () => {
    expect(ids(`    > [@a](actor:${A})`)).toEqual([]);
    expect(ids(`> Zitat\n>     [@a](actor:${A})`)).toEqual([A]);
    expect(ids(`>\t[@a](actor:${A})`)).toEqual([A]);
    expect(ids(`> \`\`\`\n> code\n[@a](actor:${A})`)).toEqual([A]);
    expect(ids(`>     code\n[@a](actor:${A})`)).toEqual([A]);
    expect(ids(`> Zitat\n\`\`\`\n[@a](actor:${A})\n\`\`\``)).toEqual([]);
    expect(ids(`[@a\n> b](actor:${A})`)).toEqual([]);
    expect(ids(`> \`\`\`\n> [@a](actor:${A})\n> \`\`\`\n> [@b](actor:${B})`)).toEqual([B]);
  });

  it('findMentions_QuoteDepthLimit_MarkersBeyondAreText', () => {
    const limit = MAX_QUOTE_DEPTH;

    expect(limit).toBe(32);
    expect(ids('>'.repeat(limit) + `     [@a](actor:${A})`)).toEqual([]);
    expect(ids('>'.repeat(limit) + ` \`\`\`\n${'>'.repeat(limit)} [@a](actor:${A})`)).toEqual([]);
    expect(ids('>'.repeat(limit + 1) + `     [@a](actor:${A})`)).toEqual([A]);
    expect(ids('>'.repeat(limit + 1) + ` \`\`\`\n[@a](actor:${A})`)).toEqual([A]);
  });

  it('findMentions_DeeplyNestedQuotes_DepthLimited_FastWithoutCrash', () => {
    const unspaced = '>'.repeat(64 * 1024) + ` [@a](actor:${A})`;
    const spaced = '> '.repeat(32_000) + `[@a](actor:${A})`;
    const manyLines =
      Array(32)
        .fill('>'.repeat(2000) + ' x')
        .join('\n') + `\n[@a](actor:${A})`;
    ids(`> [@a](actor:${A})`);

    const start = performance.now();
    const results = [ids(unspaced), ids(spaced), ids(manyLines)];
    const elapsed = performance.now() - start;

    expect(results).toEqual([[A], [A], [A]]);
    expect(elapsed).toBeLessThan(500);
  });

  it('findMentions_ManyUnclosedOpeners_Linear', () => {
    const comments = 'Text ' + '<!--'.repeat(16 * 1024) + ` [@a](actor:${A})`;
    let backticks = '';
    for (let n = 1; n <= 300; n++) {
      backticks += '`'.repeat(n) + 'x';
    }
    backticks += ` [@a](actor:${A})`;
    const links = '[x]('.repeat(16 * 1024) + ` [@a](actor:${A})`;
    ids(`[@a](actor:${A})`);

    const start = performance.now();
    const results = [ids(comments), ids(backticks), ids(links)];
    const elapsed = performance.now() - start;

    expect(results).toEqual([[A], [A], [A]]);
    expect(elapsed).toBeLessThan(1000);
  });

  it('mentionLink_NameWithBracketsOrBackticks_StillAMention', () => {
    const link = mentionLink('Bot [beta] `x`', A);

    expect(link).toBe(`[@Bot (beta) 'x'](actor:${A})`);
    expect(ids(link)).toEqual([A]);
  });

  it('mentionLink_NameWithBackslashOrCommentStart_ServerAndRendererAgree', () => {
    for (const name of ['Bot\\', 'a\\]b', 'x <!-- y', 'zwei\nZeilen']) {
      const link = mentionLink(name, A);

      expect(ids(link), name).toEqual([A]);
      expect(JSON.stringify(renderMarkdown(link)), name).toContain('"kind":"mention"');
    }
  });
});
