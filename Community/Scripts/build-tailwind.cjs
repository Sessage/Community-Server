// Tailwind 4 compiler with the existing unlayered CSS cascade retained for shared
// Razor styles and older MAUI WebViews. No Tailwind 3 JavaScript runs in this build.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const postcss = require('postcss');
const selectorParser = require('postcss-selector-parser');

const root = path.resolve(__dirname, '..');
const cliRoot = path.join(root, 'node_modules', '@tailwindcss', 'cli');
const cli = path.join(cliRoot, JSON.parse(fs.readFileSync(path.join(cliRoot, 'package.json'), 'utf8')).bin.tailwindcss);
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'sessage-tailwind-'));
const rawFile = path.join(temporary, 'tailwind.css');
const output = path.resolve(root, '../TodoSuite.Community.Shared/wwwroot/lib/tailwind/tailwind.css');
const watching = process.argv.includes('--watch');
const legacy = postcss.parse(fs.readFileSync(path.join(root, 'Styles/tailwind.compat-utilities.css'), 'utf8'));
const legacyClasses = new Set();
legacy.walkRules(rule => selectorParser(selectors => selectors.walkClasses(node => legacyClasses.add(node.value))).processSync(rule.selector));
let timer;

function publish() {
  const css = postcss.parse(fs.readFileSync(rawFile, 'utf8'));
  css.walkAtRules('layer', rule => {
    if (rule.nodes) rule.replaceWith(...rule.nodes);
    else rule.remove();
  });
  // Existing utilities keep their exact v3 declarations (including transforms,
  // sibling spacing and hover behavior). Newly used utilities are compiled by v4.
  css.walkRules(rule => {
    let existing = false;
    selectorParser(selectors => selectors.walkClasses(node => {
      if (legacyClasses.has(node.value)) existing = true;
    })).processSync(rule.selector);
    if (existing) rule.remove();
  });
  css.append(...legacy.clone().nodes);
  fs.writeFileSync(output, css.toString());
}

const watcher = watching ? fs.watch(temporary, () => {
  clearTimeout(timer);
  timer = setTimeout(() => {
    try { if (fs.existsSync(rawFile)) publish(); }
    catch (error) { console.error('Tailwind stylesheet publication failed:', error.message); }
  }, 100);
}) : null;

const args = [cli, '-i', './Styles/tailwind.input.css', '-o', rawFile, '--minify'];
if (watching) args.push('--watch=always');
const compiler = spawn(process.execPath, args, { cwd: root, stdio: 'inherit' });
compiler.on('error', error => { console.error(error); process.exitCode = 1; cleanup(); });
compiler.on('exit', code => {
  try {
    if (code === 0) {
      if (!fs.existsSync(rawFile)) throw new Error('Tailwind did not produce a stylesheet.');
      publish();
    }
  }
  catch (error) { console.error(error); code = 1; }
  process.exitCode = code ?? 1;
  cleanup();
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => compiler.kill());

function cleanup() {
  clearTimeout(timer);
  watcher?.close();
  if (fs.existsSync(rawFile)) fs.unlinkSync(rawFile);
  fs.rmdirSync(temporary);
}
