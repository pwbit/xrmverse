import { execFileSync } from 'node:child_process';
import { rmSync } from 'node:fs';
import { clearState, readState, type RunState } from './harness';

/**
 * Stops what a run started: the host's process group and the SQL Server container. Never throws. The group is
 * signalled only while one of its processes is still the host: a stale state file (a run that crashed long ago) may
 * name a pid the system has since given to an unrelated process.
 */
export function stopRun(state: RunState | undefined): void {
  if (!state) return;
  for (const pid of [state.hostPid, state.localHostPid, ...(state.extraHostPids ?? [])]) if (pid && isHostGroup(pid)) stopGroup(pid);
  if (state.container) {
    try {
      execFileSync('docker', ['rm', '-f', '-v', state.container], { stdio: 'ignore' });
    } catch {
      // already gone
    }
  }
  rmSync(state.workDir, { recursive: true, force: true });
  clearState();
}

/** True when a process of group `pgid` runs Xrmd.Host (`dotnet run --project src/Xrmd.Host` or the app it starts). */
function isHostGroup(pgid: number): boolean {
  try {
    const rows = execFileSync('ps', ['-eo', 'pgid=,args='], { encoding: 'utf8' }).split('\n');
    return rows.some((row) => {
      const match = /^\s*(\d+)\s+(.*)$/.exec(row);
      return !!match && Number(match[1]) === pgid && match[2].includes('Xrmd.Host');
    });
  } catch {
    return false; // cannot tell: never signal a group that might not be ours
  }
}

/** SIGTERM to the process group (the host was started detached, as its leader), SIGKILL if it outlives 10 s. */
function stopGroup(pid: number): void {
  const alive = () => {
    try {
      process.kill(-pid, 0);
      return true;
    } catch {
      return false;
    }
  };
  if (!alive()) return;
  process.kill(-pid, 'SIGTERM');
  const deadline = Date.now() + 10_000;
  const tick = new Int32Array(new SharedArrayBuffer(4));
  while (alive() && Date.now() < deadline) Atomics.wait(tick, 0, 0, 100);
  if (alive()) process.kill(-pid, 'SIGKILL');
}

export default async function globalTeardown(): Promise<void> {
  stopRun(readState());
}
