import * as React from 'react';
import * as Fluent from '@fluentui/react-components';
import { PcfLoadError } from './errors';

/**
 * The platform libraries a virtual control may name in its manifest (`<platform-library>`). The host does not ship the
 * platform's own bundles: it hands its React and Fluent UI v9 to the control under the globals a built control reads.
 */
export interface PlatformLibrary {
  name: string;
  version: string;
}

const major = (version: string): string => version.trim().split('.')[0] ?? '';

/** The globals each supported library is published under, or undefined when the host cannot provide it. */
function globalsFor(lib: PlatformLibrary): Record<string, unknown> | undefined {
  const name = lib.name.trim().toLowerCase();
  if (name === 'react' && major(lib.version) === '16') return { Reactv16: React };
  if (name === 'fluent' && major(lib.version) === '9') return { FluentUIReactv940: Fluent, FluentUIReactv9: Fluent };
  return undefined;
}

/** The first library the host cannot provide (Fluent 8, another React major, an unknown name), else undefined. */
export function unsupportedPlatformLibrary(libs: readonly PlatformLibrary[]): PlatformLibrary | undefined {
  return libs.find((lib) => globalsFor(lib) === undefined);
}

/**
 * Publishes the host libraries for `libs` on `win`. Throws `platformLibrary` (and publishes nothing) when one of them is not
 * supported: the control is then not loaded.
 */
export function installPlatformLibraries(libs: readonly PlatformLibrary[], win: Window = window): void {
  const unsupported = unsupportedPlatformLibrary(libs);
  if (unsupported) {
    throw new PcfLoadError('platformLibrary', `the platform library ${unsupported.name} ${unsupported.version} is not supported`);
  }
  const target = win as unknown as Record<string, unknown>;
  for (const lib of libs) Object.assign(target, globalsFor(lib));
}
