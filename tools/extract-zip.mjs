import { spawnSync } from "node:child_process";
import { inflateRawSync } from "node:zlib";
import { mkdirSync, openSync, readSync, statSync, writeFileSync, closeSync } from "node:fs";
import path from "node:path";

const [, , zipPath, outDir, ...filters] = process.argv;
if (!zipPath || !outDir) {
  console.error("Usage: node extract-zip.mjs <archive.zip> <outDir> [substringFilter...]");
  process.exit(1);
}

mkdirSync(outDir, { recursive: true });

// 1. Fast path: system unzip
const hasUnzip = spawnSync("unzip", ["-v"], { stdio: "ignore" }).status === 0;
if (hasUnzip) {
  const args =
    filters.length > 0
      ? ["-o", "-j", zipPath, ...filters.map((f) => `*${f}*`), "-d", outDir]
      : ["-o", zipPath, "-d", outDir];
  const res = spawnSync("unzip", args, { stdio: "inherit" });
  if (res.status === 0) process.exit(0);
}

// 2. Fallback: python3 zipfile
const hasPy = spawnSync("python3", ["--version"], { stdio: "ignore" }).status === 0;
if (hasPy) {
  const pyCode = `
import os, shutil, sys, zipfile
zp, out, flt = sys.argv[1], sys.argv[2], sys.argv[3:]
with zipfile.ZipFile(zp) as zf:
    for name in zf.namelist():
        if name.endswith('/'): continue
        if flt and not any(f in name for f in flt): continue
        dst = os.path.join(out, os.path.basename(name) if flt else name)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        with zf.open(name) as s, open(dst, 'wb') as d:
            shutil.copyfileobj(s, d)
`;
  const res = spawnSync("python3", ["-c", pyCode, zipPath, outDir, ...filters], { stdio: "inherit" });
  if (res.status === 0) process.exit(0);
}

// 3. Pure Node fallback (store + deflate, ZIP32 + ZIP64 EOCD)
const size = statSync(zipPath).size;
const fd = openSync(zipPath, "r");
const tailLen = Math.min(size, 65557);
const tail = Buffer.alloc(tailLen);
readSync(fd, tail, 0, tailLen, size - tailLen);

let eocd = -1;
for (let i = tailLen - 22; i >= 0; i--) {
  if (tail.readUInt32LE(i) === 0x06054b50) {
    eocd = i;
    break;
  }
}
if (eocd < 0) throw new Error(`Central directory not found in ${zipPath}`);

let cdSize = tail.readUInt32LE(eocd + 12);
let cdOffset = tail.readUInt32LE(eocd + 16);
if ((cdOffset === 0xffffffff || cdSize === 0xffffffff) && eocd >= 20 && tail.readUInt32LE(eocd - 20) === 0x07064b50) {
  const zip64EocdOffset = Number(tail.readBigUInt64LE(eocd - 12));
  const z64 = Buffer.alloc(56);
  readSync(fd, z64, 0, 56, zip64EocdOffset);
  if (z64.readUInt32LE(0) === 0x06064b50) {
    cdSize = Number(z64.readBigUInt64LE(40));
    cdOffset = Number(z64.readBigUInt64LE(48));
  }
}

const cd = Buffer.alloc(cdSize);
readSync(fd, cd, 0, cdSize, cdOffset);

let ptr = 0;
while (ptr + 46 <= cd.length && cd.readUInt32LE(ptr) === 0x02014b50) {
  const method = cd.readUInt16LE(ptr + 10);
  let compSize = cd.readUInt32LE(ptr + 20);
  let uncompSize = cd.readUInt32LE(ptr + 24);
  const nameLen = cd.readUInt16LE(ptr + 28);
  const extraLen = cd.readUInt16LE(ptr + 30);
  const commentLen = cd.readUInt32LE(ptr + 32) & 0xffff;
  let localHeaderOffset = cd.readUInt32LE(ptr + 42);
  const entryName = cd.subarray(ptr + 46, ptr + 46 + nameLen).toString("utf8");
  const extra = cd.subarray(ptr + 46 + nameLen, ptr + 46 + nameLen + extraLen);
  ptr += 46 + nameLen + extraLen + commentLen;

  if (compSize === 0xffffffff || uncompSize === 0xffffffff || localHeaderOffset === 0xffffffff) {
    let ep = 0;
    while (ep + 4 <= extra.length) {
      const id = extra.readUInt16LE(ep);
      const sz = extra.readUInt16LE(ep + 2);
      if (id === 0x0001) {
        let zp = ep + 4;
        if (uncompSize === 0xffffffff) { uncompSize = Number(extra.readBigUInt64LE(zp)); zp += 8; }
        if (compSize === 0xffffffff) { compSize = Number(extra.readBigUInt64LE(zp)); zp += 8; }
        if (localHeaderOffset === 0xffffffff) { localHeaderOffset = Number(extra.readBigUInt64LE(zp)); }
        break;
      }
      ep += 4 + sz;
    }
  }

  if (entryName.endsWith("/")) continue;
  if (filters.length > 0 && !filters.some((f) => entryName.includes(f))) continue;

  const lhdr = Buffer.alloc(30);
  readSync(fd, lhdr, 0, 30, localHeaderOffset);
  const lNameLen = lhdr.readUInt16LE(26);
  const lExtraLen = lhdr.readUInt16LE(28);
  const dataOffset = localHeaderOffset + 30 + lNameLen + lExtraLen;

  const compBuf = Buffer.alloc(compSize);
  readSync(fd, compBuf, 0, compSize, dataOffset);
  const raw = method === 0 ? compBuf : method === 8 ? inflateRawSync(compBuf) : null;
  if (!raw) throw new Error(`Unsupported compression method ${method} for ${entryName}`);

  const targetName = filters.length > 0 ? path.basename(entryName) : entryName;
  const dst = path.join(outDir, targetName);
  mkdirSync(path.dirname(dst), { recursive: true });
  writeFileSync(dst, raw);
}

closeSync(fd);
