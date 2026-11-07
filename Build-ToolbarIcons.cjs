const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');

async function main() {
  const folder = path.join(__dirname, 'assets', 'toolbar');
  // Keep the embedded resource names stable while using the approved Rounded sources.
  const icons = new Map([
    ['undo-2', 'undo'], ['refresh-cw', 'refresh'], ['pause', 'pause'],
    ['play', 'play'], ['plus', 'plus'], ['keyboard', 'keyboard'], ['info', 'info']
  ]);
  const requested = process.argv.slice(2);
  const names = requested.length ? requested : [...icons.keys()];
  for (const name of names) {
    if (!icons.has(name)) throw new Error(`Unknown toolbar icon: ${name}`);
  }
  await fs.mkdir(folder, { recursive: true });
  for (const name of names) {
    const svg = await fs.readFile(path.join(folder, 'svg', `${icons.get(name)}.svg`), 'utf8');
    for (const [mode, color] of [['light', '#263238'], ['dark', '#e6e9ec']]) {
      const iconColor = name === 'info' ? (mode === 'dark' ? '#8f9aa2' : '#65727b') : color;
      await sharp(Buffer.from(svg.replace(/currentColor/g, iconColor)), { density: 192 })
        .resize(32, 32).png().toFile(path.join(folder, `${name}-${mode}.png`));
    }
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
