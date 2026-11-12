// Allowlist HTML -> plain node tree. The page renders the tree with React elements, so no raw HTML ever reaches
// the DOM: unknown tags are dropped (their text kept), script/style bodies are dropped, and no attribute survives.

export type SafeNode = string | { tag: SafeTag; children: SafeNode[] };

const TAGS = ['p', 'ul', 'ol', 'li', 'strong', 'em', 'br', 'h3', 'h4', 'table', 'thead', 'tbody', 'tr', 'th', 'td'] as const;
export type SafeTag = (typeof TAGS)[number];
const ALLOWED = new Set<string>(TAGS);
const DROP_BODY = new Set(['script', 'style', 'iframe', 'object', 'noscript', 'template', 'textarea', 'title']);
const MAX_DEPTH = 12;
const MAX_NODES = 5000;

const ENTITIES: Record<string, string> = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

function decode(text: string): string {
  return text.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (m, e: string) => {
    if (e[0] === '#') {
      const code = e[1] === 'x' || e[1] === 'X' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
      return Number.isInteger(code) && code > 0 && code <= 0x10ffff && !(code >= 0xd800 && code <= 0xdfff) ? String.fromCodePoint(code) : '';
    }
    return ENTITIES[e.toLowerCase()] ?? m;
  });
}

// A tag is `<`, optional `/`, a name, then anything up to `>` (quoted attribute values may contain `>`).
const TOKEN = /<!--[\s\S]*?(?:-->|$)|<(\/?)([a-zA-Z][a-zA-Z0-9]*)((?:"[^"]*"|'[^']*'|[^'">])*)>?/g;

export function sanitizeHtml(input: string): SafeNode[] {
  const root: { tag: SafeTag | null; children: SafeNode[] } = { tag: null, children: [] };
  const stack = [root];
  let count = 0;
  let last = 0;
  let skipUntil: string | null = null;
  const top = () => stack[stack.length - 1]!;
  const addText = (raw: string) => {
    const text = decode(raw.replace(/</g, ''));
    if (text && count++ < MAX_NODES) top().children.push(text);
  };

  for (const m of input.matchAll(TOKEN)) {
    const index = m[0] === undefined ? 0 : (m.index ?? 0);
    if (skipUntil === null) addText(input.slice(last, index));
    last = index + m[0].length;
    const closing = m[1] === '/';
    const name = m[2]?.toLowerCase();
    if (!name) continue; // comment
    if (skipUntil !== null) {
      if (closing && name === skipUntil) skipUntil = null;
      continue;
    }
    if (DROP_BODY.has(name)) {
      if (!closing && !/\/\s*$/.test(m[3] ?? '')) skipUntil = name;
      continue;
    }
    if (!ALLOWED.has(name) || count > MAX_NODES) continue;
    const tag = name as SafeTag;
    if (tag === 'br') {
      if (!closing) top().children.push({ tag, children: [] });
      continue;
    }
    if (closing) {
      for (let i = stack.length - 1; i > 0; i--) {
        if (stack[i]!.tag === tag) {
          stack.length = i;
          break;
        }
      }
      continue;
    }
    if (tag === 'p' || tag === 'li') {
      // implied end tag: a new <p>/<li> closes an open one of the same kind
      const open = stack.findIndex((n, i) => i > 0 && n.tag === tag);
      if (open > 0) stack.length = open;
    }
    if (stack.length > MAX_DEPTH) continue;
    const node = { tag, children: [] as SafeNode[] };
    top().children.push(node);
    count++;
    stack.push(node);
  }
  if (skipUntil === null) addText(input.slice(last));
  return root.children;
}

/** Plain text of a node list (for tests and fallbacks). */
export function nodesToText(nodes: SafeNode[]): string {
  return nodes.map((n) => (typeof n === 'string' ? n : nodesToText(n.children))).join('');
}
