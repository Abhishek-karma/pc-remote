// Generates windows-agent/app.ico — the PC Remote brand icon (amber mouse on
// the dark blue-steel tile, same design as the Android launcher icon).
// Pure Node (zlib only): draws the geometry per pixel with supersampling,
// encodes RGBA PNG frames, and packs them into a Windows .ico container.
// Run: node icon-gen.cjs   (from windows-agent/)
'use strict';
const fs = require('fs');
const zlib = require('zlib');

// --- CRC32 (PNG requirement) ---
const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();
function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}
function pngEncode(width, height, rgba) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;  // bit depth
  ihdr[9] = 6;  // color type RGBA
  const raw = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * 4 + 1)] = 0; // filter: none
    rgba.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// --- Brand colors (ui/theme/Colors.kt) ---
const BG = [14, 18, 22];        // 0x0E1216
const BODY = [232, 180, 90];    // 0xE8B45A
const SHELL = [243, 210, 140];  // 0xF3D28C
const SPLIT = BG;
const WHEEL = [36, 27, 5];      // 0x241B05

function drawFrame(S) {
  const SS = 4; // supersampling
  const rgba = Buffer.alloc(S * S * 4);
  const cx = S / 2, cy = S / 2;
  const a = 0.18 * S, b = 0.28 * S;          // capsule half-axes (0.36 x 0.56)
  const splitY = cy - b + 0.4 * (2 * b);      // top shell is 40% of the capsule
  const r = 0.2 * S;                          // tile corner radius
  const wheelW = 0.055 * S, wheelTop = cy - b + 0.12 * S, wheelH = 0.16 * S;

  const inCapsule = (x, y) => {
    const dx = (x - cx) / a, dy = (y - cy) / b;
    return dx * dx + dy * dy <= 1;
  };
  const inTile = (x, y) => {
    if (x < 0 || y < 0 || x > S || y > S) return false;
    const dx = Math.max(r - x, 0, x - (S - r)), dy = Math.max(r - y, 0, y - (S - r));
    return dx * dx + dy * dy <= r * r;
  };
  const inWheel = (x, y) =>
    Math.abs(x - cx) <= wheelW / 2 && y >= wheelTop && y <= wheelTop + wheelH;

  for (let py = 0; py < S; py++) {
    for (let px = 0; px < S; px++) {
      let c = null, cover = 0;
      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          const x = px + (sx + 0.5) / SS, y = py + (sy + 0.5) / SS;
          if (!inTile(x, y)) continue;
          cover++;
          if (!inCapsule(x, y)) { c = BG; continue; }
          if (Math.abs(y - splitY) < 0.012 * S) { c = SPLIT; continue; }
          if (inWheel(x, y)) { c = WHEEL; continue; }
          c = y < splitY ? SHELL : BODY;
        }
      }
      const i = (py * S + px) * 4;
      if (c && cover > 0) {
        rgba[i] = c[0]; rgba[i + 1] = c[1]; rgba[i + 2] = c[2];
        rgba[i + 3] = Math.round((cover / (SS * SS)) * 255);
      }
    }
  }
  return rgba;
}

// --- Pack the .ico (PNG-compressed frames, Vista+) ---
const sizes = [16, 32, 48, 64, 128, 256];
const frames = sizes.map((s) => ({ s, png: pngEncode(s, s, drawFrame(s)) }));
const header = Buffer.alloc(6);
header.writeUInt16LE(0, 0);
header.writeUInt16LE(1, 2); // type: icon
header.writeUInt16LE(frames.length, 4);
const dir = Buffer.alloc(16 * frames.length);
let offset = header.length + dir.length;
frames.forEach((f, i) => {
  const e = i * 16;
  dir[e] = f.s >= 256 ? 0 : f.s;
  dir[e + 1] = f.s >= 256 ? 0 : f.s;
  dir[e + 2] = 0; // colors
  dir[e + 3] = 0; // reserved
  dir.writeUInt16LE(1, e + 4);  // planes
  dir.writeUInt16LE(32, e + 6); // bpp
  dir.writeUInt32LE(f.png.length, e + 8);
  dir.writeUInt32LE(offset, e + 12);
  offset += f.png.length;
});
const ico = Buffer.concat([header, dir, ...frames.map((f) => f.png)]);
fs.writeFileSync(__dirname + '/app.ico', ico);
console.log(`app.ico written: ${ico.length} bytes, sizes ${sizes.join(', ')}`);
