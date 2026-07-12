import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const catalog = fs.readFileSync(
  path.join(root, 'mods/bepinex/src/Save/RuntimeStaticDataCatalog.cs'),
  'utf8',
);

for (const removedProbe of [
  'GetSpecialFoodTagLang',
  'GetSpecialBevTagLang',
  'specialFoodText=',
  'specialBevText=',
  'ResolveLanguageDictionary(',
]) {
  assert.ok(!catalog.includes(removedProbe),
    `Runtime catalog must not call the unused warning-producing probe: ${removedProbe}`);
}

for (const requiredRuntimeField of [
  'GetMemberValue(guest, "LikeFoodTag")',
  'GetMemberValue(guest, "LikeFoodTagOriginal")',
  'GetMemberValue(guest, "HateFoodTag")',
  'GetMemberValue(guest, "HateFoodTagOriginal")',
  'GetMemberValue(guest, "LikeBevTag")',
  'FormatMember(guest, "SpawnType")',
  'localPlaces=',
  'ParseSectionRows(guestLines, "SpecialGuests")',
]) {
  assert.ok(catalog.includes(requiredRuntimeField),
    `Runtime catalog lost a functional rare-guest data source: ${requiredRuntimeField}`);
}

console.log('PASS: runtime catalog avoids unused language probes while retaining functional rare-guest tags.');
