const SOURCE_ROOT = new URL('../apps/companion/src/', import.meta.url);
const SOURCE_ALIAS_PATTERN = /^[a-zA-Z0-9_./-]+$/;

export async function resolve(specifier, context, nextResolve) {
  if (!specifier.startsWith('@/')) {
    return nextResolve(specifier, context);
  }

  const sourcePath = specifier.slice(2);
  if (!SOURCE_ALIAS_PATTERN.test(sourcePath)
      || sourcePath.split('/').some((segment) => segment === '..')) {
    throw new Error(`Invalid source alias in mission audit: ${specifier}`);
  }

  return {
    shortCircuit: true,
    url: new URL(`${sourcePath}.ts`, SOURCE_ROOT).href,
  };
}
