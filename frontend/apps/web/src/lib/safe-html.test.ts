import test from 'node:test';
import assert from 'node:assert/strict';
import { sanitizeHtml, nodesToText, type SafeNode } from './safe-html.ts';

const tags = (nodes: SafeNode[]): string[] => nodes.flatMap((n) => (typeof n === 'string' ? [] : [n.tag, ...tags(n.children)]));

test('keeps allowlisted structure', () => {
  const out = sanitizeHtml('<p>Hi <strong>there</strong></p><ul><li>a</li><li>b</li></ul>');
  assert.deepEqual(tags(out), ['p', 'strong', 'ul', 'li', 'li']);
  assert.equal(nodesToText(out), 'Hi thereab');
});

test('script is removed with its body', () => {
  const out = sanitizeHtml('<p>a</p><script>alert(1)</script><p>b</p>');
  assert.equal(nodesToText(out), 'ab');
  assert.deepEqual(tags(out), ['p', 'p']);
});

test('img onerror: tag dropped, no attribute survives', () => {
  const out = sanitizeHtml('<p>x<img src=x onerror=alert(1)>y</p>');
  assert.equal(nodesToText(out), 'xy');
  assert.deepEqual(tags(out), ['p']);
});

test('attributes on allowed tags are discarded', () => {
  const out = sanitizeHtml('<p onclick="evil()" style="x" class="c">t</p>');
  assert.deepEqual(out, [{ tag: 'p', children: ['t'] }]);
});

test('javascript: links lose the anchor, keep the text', () => {
  const out = sanitizeHtml('<a href="javascript:alert(1)">click</a>');
  assert.deepEqual(out, ['click']);
});

test('quoted > inside an attribute does not leak markup', () => {
  const out = sanitizeHtml('<p title="a>b<script>x</script>">ok</p>');
  assert.equal(nodesToText(out), 'ok');
});

test('malformed and nested tags do not throw or escape the tree', () => {
  const out = sanitizeHtml('<p><strong>a<em>b</p></em>c</strong><li>x<ul><li>y');
  assert.equal(nodesToText(out).replace(/\s/g, ''), 'abcxy');
  assert.ok(tags(out).every((t) => ['p', 'strong', 'em', 'li', 'ul'].includes(t)));
});

test('unterminated tag and stray < are text, never markup', () => {
  assert.equal(nodesToText(sanitizeHtml('1 < 2 and <p>x')), '1  2 and x');
  assert.equal(nodesToText(sanitizeHtml('<scr<script>ipt>alert(1)')), 'ipt>alert(1)');
});

test('entities decode to text only', () => {
  assert.equal(nodesToText(sanitizeHtml('<p>&lt;script&gt; &amp; &#60;b&#62;</p>')), '<script> & <b>');
});

test('comments and style bodies vanish', () => {
  assert.equal(nodesToText(sanitizeHtml('a<!-- <b>x</b> -->b<style>p{}</style>c')), 'abc');
});

test('tables survive', () => {
  const out = sanitizeHtml('<table><thead><tr><th>A</th></tr></thead><tbody><tr><td>1</td></tr></tbody></table>');
  assert.deepEqual(tags(out), ['table', 'thead', 'tr', 'th', 'tbody', 'tr', 'td']);
});

test('depth is bounded', () => {
  const out = sanitizeHtml('<ul>'.repeat(500) + 'deep');
  let depth = 0;
  let cur: SafeNode[] = out;
  while (cur.length && typeof cur[0] !== 'string') {
    depth++;
    cur = (cur[0] as { children: SafeNode[] }).children;
  }
  assert.ok(depth <= 13);
});
