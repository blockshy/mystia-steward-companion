import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';

// 使用既有人工规模目录连续模拟1/4/8个客人的到店；不接触真实游戏、存档或网络。
// 每个请求交给同一个真实领域进程，保留缓存；记录输出摘要，便于逐字节比较优化前后结果。
const fixture = JSON.parse(readFileSync('temp/csharp-business-load-fixture.json', 'utf8'));
const baseline = process.argv.includes('--before');
const directory = 'temp/flutter-order-assets-performance-20261011';
mkdirSync(directory, { recursive: true });
const requests = [];
function add(label, payload) {
  requests.push({ label, operation: 'orders-cached', args: structuredClone(payload) });
}
const payload = structuredClone(fixture);
const order = payload.orders[0];
payload.activeRareGuests = [{ guestId: order.guestId, deskCode: order.deskCode, guestName: order.guestName, fund: 200, willPayMoney: true }];
add('one-cold', payload);
add('one-warm', payload);
payload.activeRareGuests.push({ guestId: 999, deskCode: 99, guestName: '无关客人', fund: 500, willPayMoney: true });
add('unrelated-arrival', payload);
payload.activeRareGuests[1].fund = 490;
payload.activeRareGuests[1].lastSeenAtUtc = 'observed-new';
add('unrelated-observation', payload);
payload.activeRareGuests[0].fund = 150;
add('own-budget-change', payload);
for (const count of [4, 8]) {
  payload.orders = Array.from({ length: count }, (_, index) => ({ ...order, traceId: `R-${index + 1}`, orderLifecycleSequence: index + 1, deskCode: index + 1 }));
  add(`${count}-arrival`, payload);
  add(`${count}-warm`, payload);
  payload.activeRareGuests[1].lastSeenAtUtc = `unrelated-${count}`;
  add(`${count}-unrelated`, payload);
}
// 必需输入变化必须仍重算，不能为了缓存命中忽略库存、配置和目录。
payload.runtime.ownedIngredientQty['1'] = 0;
add('inventory-change', payload);
const run = spawnSync('dotnet', ['tests/csharp-business-orders/bin/Release/net6.0/CSharpBusinessOrders.dll'], {
  input: requests.map(({ operation, args }) => JSON.stringify({ operation, args })).join('\n') + '\n',
  encoding: 'utf8', windowsHide: true, maxBuffer: 128 * 1024 * 1024, timeout: 180000,
});
assert.equal(run.status, 0, run.stderr || run.error?.message);
const responses = run.stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
assert.equal(responses.length, requests.length);
const rows = responses.map((response, index) => {
  assert.equal(response.ok, true, response.error);
  return { label: requests[index].label, elapsedMs: response.elapsedMs, allocatedBytes: response.allocatedBytes,
    cacheHits: response.cacheHits, cacheMisses: response.cacheMisses,
    resultSha256: createHash('sha256').update(JSON.stringify(response.result)).digest('hex') };
});
if (!baseline) {
  for (const index of [2, 3, 7, 10]) {
    assert.equal(rows[index].cacheMisses, rows[index - 1].cacheMisses, `${rows[index].label}不能使已有订单缓存失效`);
    assert.equal(rows[index].resultSha256, rows[index - 1].resultSha256, '无关客人变化不得改变输出');
  }
  assert.ok(rows[4].cacheMisses > rows[3].cacheMisses, '当前客人的预算变化必须重新计算');
  assert.ok(rows.at(-1).cacheMisses > rows.at(-2).cacheMisses, '库存变化必须重新计算');
  // 本机已保存改动前数据时额外比较全部输出；全新检出不依赖未纳管证据，仍执行上方
  // 当前语义/失效断言。完整语义等价由纳管的 differential.mjs 冻结 oracle 负责。
  if (existsSync(`${directory}/arrival-before.json`)) {
    const before = JSON.parse(readFileSync(`${directory}/arrival-before.json`, 'utf8'));
    assert.deepEqual(rows.map(x => [x.label, x.resultSha256]), before.map(x => [x.label, x.resultSha256]), '优化前后全部结果须完全一致');
  }
}
writeFileSync(`${directory}/arrival-${baseline ? 'before' : 'after'}.json`, JSON.stringify(rows, null, 2) + '\n');
console.log(JSON.stringify(rows, null, 2));
