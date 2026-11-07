const fs = require('node:fs');
const path = require('node:path');
const sharp = require('sharp');
const repoRoot = path.resolve(__dirname, '..');

async function main() {
  const source = path.join(repoRoot, 'assets', 'taskbar-select.svg');
  const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
  const images = [];
  for (const size of sizes) {
    const rgba = await sharp(source, { density: 384 }).resize(size, size).ensureAlpha().raw().toBuffer();
    const maskStride = Math.ceil(size / 32) * 4;
    const pixels = size * size * 4;
    const dib = Buffer.alloc(40 + pixels + maskStride * size);
    dib.writeUInt32LE(40, 0);
    dib.writeInt32LE(size, 4);
    dib.writeInt32LE(size * 2, 8);
    dib.writeUInt16LE(1, 12);
    dib.writeUInt16LE(32, 14);
    dib.writeUInt32LE(pixels, 20);
    // Classic BGRA/DIB icon frames also work with .NET Framework Icon.ToBitmap.
    for (let y = 0; y < size; y++) {
      for (let x = 0; x < size; x++) {
        const from = (y * size + x) * 4;
        const to = 40 + ((size - y - 1) * size + x) * 4;
        dib[to] = rgba[from + 2]; dib[to + 1] = rgba[from + 1];
        dib[to + 2] = rgba[from]; dib[to + 3] = rgba[from + 3];
        if (rgba[from + 3] === 0) dib[40 + pixels + (size - y - 1) * maskStride + (x >> 3)] |= 0x80 >> (x & 7);
      }
    }
    images.push(dib);
  }
  const directory = Buffer.alloc(6 + 16 * sizes.length);
  directory.writeUInt16LE(1, 2);
  directory.writeUInt16LE(sizes.length, 4);
  let offset = directory.length;
  sizes.forEach((size, i) => {
    const entry = 6 + i * 16;
    directory[entry] = size === 256 ? 0 : size;
    directory[entry + 1] = size === 256 ? 0 : size;
    directory.writeUInt16LE(1, entry + 4);
    directory.writeUInt16LE(32, entry + 6);
    directory.writeUInt32LE(images[i].length, entry + 8);
    directory.writeUInt32LE(offset, entry + 12);
    offset += images[i].length;
  });
  fs.writeFileSync(path.join(repoRoot, 'assets', 'TaskbarSelect.ico'), Buffer.concat([directory, ...images]));
  await sharp(source, { density: 384 }).resize(256, 256).png().toFile(path.join(repoRoot, 'assets', 'taskbar-select-preview.png'));
  console.log(`Built icon at ${sizes.join(', ')} pixels.`);
}
main().catch(error => { console.error(error); process.exitCode = 1; });
