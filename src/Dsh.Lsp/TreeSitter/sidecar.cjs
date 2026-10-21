const fs = require('node:fs');
const path = require('node:path');

const request = JSON.parse(fs.readFileSync(0, 'utf8'));
const runtime = request.runtimePath;
const candidates = [
  path.join(runtime, 'node_modules', 'web-tree-sitter', 'web-tree-sitter.cjs'),
  path.join(runtime, 'node_modules', 'web-tree-sitter', 'tree-sitter.cjs'),
  path.join(runtime, 'web-tree-sitter.cjs'),
];

function fail(error) {
  fs.writeSync(1, JSON.stringify({ ok: false, error: String(error && error.stack || error) }));
  process.exitCode = 0;
}

(async () => {
  let parser = null;
  let tree = null;
  let language = null;
  try {
    const runtimeEntry = candidates.find(candidate => fs.existsSync(candidate));
    if (!runtimeEntry) throw new Error('web-tree-sitter runtime not found under RuntimePath');
    const { Parser, Language } = require(runtimeEntry);
    await Parser.init();
    language = await Language.load(request.grammar);
    parser = new Parser();
    parser.setLanguage(language);
    tree = parser.parse(request.source);

    function convert(node, depth) {
      if (!node || depth > 512) return null;
      const children = [];
      for (let index = 0; index < node.childCount; index++) {
        const child = node.child(index);
        const converted = convert(child, depth + 1);
        if (!converted) continue;
        try {
          converted.fieldName = node.fieldNameForChild(index) || null;
        } catch {
          converted.fieldName = null;
        }
        children.push(converted);
      }
      return {
        type: node.type,
        fieldName: null,
        isNamed: node.isNamed,
        isMissing: node.isMissing,
        isExtra: Boolean(node.isExtra),
        startByte: node.startIndex,
        endByte: node.endIndex,
        children,
      };
    }

    const root = convert(tree.rootNode, 0);
    fs.writeSync(1, JSON.stringify({ ok: true, root }));
  } catch (error) {
    if (request.language === 'xml') {
      fs.writeSync(1, JSON.stringify({ ok: true, root: xmlFallback(request.source) }));
    } else {
      fail(error);
    }
  } finally {
    try { if (tree) tree.delete(); } catch {}
    try { if (parser) parser.delete(); } catch {}
    try { if (language) language.delete(); } catch {}
    process.exitCode = 0;
  }
})();

function xmlFallback(source) {
  const root = { type: 'document', fieldName: null, isNamed: true, isMissing: false, isExtra: false, startByte: 0, endByte: Buffer.byteLength(source), children: [] };
  const stack = [root];
  const tag = /<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<\?[\s\S]*?\?>|<\/\s*([A-Za-z_][\w:.-]*)\s*>|<\s*([A-Za-z_][\w:.-]*)(?:\s[^<>]*?)?\/\s*>|<\s*([A-Za-z_][\w:.-]*)(?:\s[^<>]*?)?>|([^<]+)/g;
  let match;
  while ((match = tag.exec(source)) !== null) {
    const text = match[0];
    const startByte = Buffer.byteLength(source.slice(0, match.index));
    const endByte = startByte + Buffer.byteLength(text);
    const isOpen = match[3] !== undefined;
    const isClose = match[1] !== undefined;
    const isSelfClosing = match[2] !== undefined;
    const isText = match[4] !== undefined;
    const isSpecial = text.startsWith('<!--') || text.startsWith('<![CDATA[') || text.startsWith('<?');
    if (isText) {
      if (text.trim().length > 0)
        stack[stack.length - 1].children.push({ type: 'text', fieldName: null, isNamed: true, isMissing: false, isExtra: false, startByte, endByte, children: [] });
      continue;
    }
    if (isSpecial || isClose) {
      if (isClose && stack.length > 1)
        stack.pop();
      continue;
    }
    const name = match[3] || match[2];
    const node = { type: name, fieldName: null, isNamed: true, isMissing: false, isExtra: false, startByte, endByte, children: [] };
    stack[stack.length - 1].children.push(node);
    if (isOpen && !isSelfClosing)
      stack.push(node);
  }
  return root;
}
