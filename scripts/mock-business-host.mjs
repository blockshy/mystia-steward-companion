import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

/**
 * UI 模拟 API 的 C# 业务桥。只启动仓库内的 source-link 离线宿主，绝不读取游戏安装或真实端口。
 * JSONL 请求按编号关联；库存、目录与生效配置来自 mock fixture，计算使用生产业务程序集。
 */
export class MockBusinessHost {
  #child;
  #pending = new Map();
  #nextId = 0;
  #initialized = false;
  #inputSignature = '';
  #serial = Promise.resolve();

  async request(operation, payload, input) {
    // publish 与业务查询必须顺序执行，避免多个页面把初始化或输入更新互相覆盖。
    const work = this.#serial.then(async () => {
      this.#start();
      if (!this.#initialized) {
        await this.#send('initialize', { clientId: 'mock-business-primary', profile: input.profile });
        this.#initialized = true;
      }
      // 同步 Node 模拟设备表已经确认的在线活动；这条消息不授予或续期自动化租约。
      if (input.primaryOnline) await this.#send('heartbeat', {});
      const signature = JSON.stringify([input.snapshot.snapshotSignature, input.catalog.signature, input.profile, input.favorites, input.customRecipes]);
      if (this.#inputSignature !== signature) {
        await this.#send('publish', input);
        this.#inputSignature = signature;
      }
      return this.#send(operation, payload);
    });
    this.#serial = work.catch(() => undefined);
    return work;
  }

  #start() {
    if (this.#child) return;
    const assembly = fileURLToPath(new URL('../tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', import.meta.url));
    const child = spawn('dotnet', [assembly, '--serve'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    this.#child = child;
    child.stderr.on('data', (data) => process.stderr.write(`[mock C# host] ${data}`));
    createInterface({ input: child.stdout }).on('line', (line) => {
      let response;
      try { response = JSON.parse(line); } catch { process.stderr.write(`[mock C# host] invalid JSONL: ${line}\n`); return; }
      const pending = this.#pending.get(response.id);
      if (!pending) return;
      this.#pending.delete(response.id);
      clearTimeout(pending.timer);
      if (response.ok) pending.resolve(response.result);
      else pending.reject(new Error(response.error || 'C# 离线宿主请求失败。'));
    });
    const stop = (message) => {
      if (this.#child !== child) return;
      this.#child = undefined;
      this.#initialized = false;
      this.#inputSignature = '';
      for (const pending of this.#pending.values()) { clearTimeout(pending.timer); pending.reject(new Error(message)); }
      this.#pending.clear();
    };
    child.on('error', (error) => stop(error.message));
    child.on('exit', (code) => stop(`C# 离线宿主退出（${code}）。请先构建 tests/csharp-business-host。`));
  }

  #send(operation, payload) {
    return new Promise((resolve, reject) => {
      const id = ++this.#nextId;
      const timer = setTimeout(() => { this.#pending.delete(id); reject(new Error('C# 离线宿主响应超时。')); }, 8000);
      this.#pending.set(id, { resolve, reject, timer });
      this.#child.stdin.write(`${JSON.stringify({ id, operation, ...payload })}\n`);
    });
  }

  close() {
    this.#child?.stdin.end();
    this.#child?.kill();
  }
}
