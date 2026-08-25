import { existsSync } from 'node:fs';
import path from 'node:path';

function isCanonicalJavaVersion(value) {
  return typeof value === 'string' && /^\d+\.\d+\.\d+(?:\.\d+)?$/u.test(value);
}

function isCanonicalJavaReleaseSemver(value) {
  return typeof value === 'string'
    && /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*$/u.test(value);
}

function matchesLockedJavaRuntimeVersion(lockedVersion, runtimeVersion) {
  if (!isCanonicalJavaVersion(lockedVersion) || typeof runtimeVersion !== 'string') return false;

  const escapedVersion = lockedVersion.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  return new RegExp(`^${escapedVersion}(?:$|[+_-])`, 'u').test(runtimeVersion);
}

function resolveAndroidJavaCommand({
  javaHome = process.env.JAVA_HOME,
  platform = process.platform,
  pathExists = existsSync,
} = {}) {
  const configuredJavaHome = typeof javaHome === 'string' ? javaHome.trim() : '';
  if (!configuredJavaHome) {
    throw new Error('JAVA_HOME must point to the locked Android Temurin JDK.');
  }

  const pathImplementation = platform === 'win32' ? path.win32 : path.posix;
  const executableName = platform === 'win32' ? 'java.exe' : 'java';
  const command = pathImplementation.join(configuredJavaHome, 'bin', executableName);
  if (!pathExists(command)) {
    throw new Error(`Locked Android JDK executable does not exist: ${command}`);
  }

  return {
    command,
    javaHome: configuredJavaHome,
  };
}

export {
  isCanonicalJavaReleaseSemver,
  isCanonicalJavaVersion,
  matchesLockedJavaRuntimeVersion,
  resolveAndroidJavaCommand,
};
