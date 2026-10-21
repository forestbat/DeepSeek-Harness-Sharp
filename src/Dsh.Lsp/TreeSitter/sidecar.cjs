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
  process.stdout.write(JSON.stringify({ ok: false, error: String(error && error.stack || error) }));
  process.exit(0);
}

(async () => {
  try {
    const runtimeEntry = candidates.find(candidate => fs.existsSync(candidate));
    if (!runtimeEntry) throw new Error('web-tree-sitter runtime not found under RuntimePath');
    const { Parser, Language } = require(runtimeEntry);
    await Parser.init();
    const language = await Language.load(request.grammar);
    const parser = new Parser();
    parser.setLanguage(language);
    const tree = parser.parse(request.source);

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
    process.stdout.write(JSON.stringify({ ok: true, root }));
    process.exit(0);
  } catch (error) {
    fail(error);
  }
})();
