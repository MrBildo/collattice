import { describe, test, expect } from 'vitest';
import { buildMarkdownSnippet } from './markdown-snippet';

describe('buildMarkdownSnippet', () => {
  test('drops emphasis, heading and list markers but keeps their words', () => {
    expect(buildMarkdownSnippet('**This is a sample card.** It has _two_ sentences.', 100)).toBe(
      'This is a sample card. It has two sentences.',
    );
    expect(
      buildMarkdownSnippet('# Heading line\n- first bullet\n- second ~~old~~ bullet', 100),
    ).toBe('Heading line first bullet second old bullet');
  });

  test('keeps link text, code and image alt text, and drops URLs and raw HTML tags', () => {
    expect(
      buildMarkdownSnippet(
        'See [the docs](https://example.com) and `AuthGate`. ![login screen](a.png) <b>bold</b>',
        100,
      ),
    ).toBe('See the docs and AuthGate. login screen bold');
  });

  test('leaves underscores and asterisks inside words and sums alone', () => {
    expect(buildMarkdownSnippet('Rename snake_case_name; 2 * 3 * 4 is 24', 100)).toBe(
      'Rename snake_case_name; 2 * 3 * 4 is 24',
    );
  });

  test('keeps the words of separate paragraphs and table cells apart', () => {
    expect(buildMarkdownSnippet('First paragraph.\n\nSecond one.', 100)).toBe(
      'First paragraph. Second one.',
    );
    expect(buildMarkdownSnippet('| Name | Size |\n| --- | --- |\n| Card | M |', 100)).toBe(
      'Name Size Card M',
    );
  });

  test('cuts to the requested length after the markup is gone', () => {
    expect(buildMarkdownSnippet('**abcdefghij** klm', 5)).toBe('abcde');
  });

  test('returns an empty string when nothing but markup remains', () => {
    expect(buildMarkdownSnippet('<div></div>\n\n[ref]: https://example.com', 100)).toBe('');
  });
});
