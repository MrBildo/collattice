import remarkGfm from 'remark-gfm';
import remarkParse from 'remark-parse';
import { unified } from 'unified';
import type { Nodes } from 'mdast';

// The same parser and Markdown flavour the card view renders with, so a snippet
// drops exactly the syntax the card itself would not show as text.
const parser = unified().use(remarkParse).use(remarkGfm);

// A snippet is one short line, so only the start of a description can reach it.
// Parsing a long description whole would be wasted work on every search.
const PARSE_LIMIT = 1000;

const BLOCK_TYPES = new Set<Nodes['type']>([
  'blockquote',
  'code',
  'heading',
  'list',
  'listItem',
  'paragraph',
  'table',
  'tableCell',
  'tableRow',
]);

function collectText(node: Nodes, parts: string[]) {
  switch (node.type) {
    case 'text':
    case 'inlineCode':
    case 'code':
      parts.push(node.value);
      break;
    case 'image':
      parts.push(node.alt ?? '');
      break;
    case 'break':
      parts.push(' ');
      break;
    // Raw HTML tags and link definitions are markup, not prose.
    case 'html':
    case 'definition':
    case 'footnoteDefinition':
      return;
    default:
      if ('children' in node) {
        for (const child of node.children) {
          collectText(child, parts);
        }
      }
  }

  // Keep the words of neighbouring blocks apart once their line breaks are gone.
  if (BLOCK_TYPES.has(node.type)) {
    parts.push(' ');
  }
}

// A card description as one line of plain text, for places that show a preview
// of it rather than the rendered card.
export function buildMarkdownSnippet(markdown: string, maxLength: number): string {
  const parts: string[] = [];
  collectText(parser.parse(markdown.slice(0, PARSE_LIMIT)), parts);
  return parts.join('').replace(/\s+/g, ' ').trim().slice(0, maxLength);
}
