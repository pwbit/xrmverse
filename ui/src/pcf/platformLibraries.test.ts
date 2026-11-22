import * as React from 'react';
import * as Fluent from '@fluentui/react-components';
import { afterEach, describe, expect, it } from 'vitest';
import { installPlatformLibraries, unsupportedPlatformLibrary } from './platformLibraries';
import { PcfLoadError } from './errors';

const globals = window as unknown as Record<string, unknown>;

afterEach(() => {
  delete globals.Reactv16;
  delete globals.FluentUIReactv940;
  delete globals.FluentUIReactv9;
});

describe('installPlatformLibraries', () => {
  it('maps_react_16_and_fluent_9_to_the_host_libraries', () => {
    installPlatformLibraries([
      { name: 'React', version: '16.14.0' },
      { name: 'Fluent', version: '9.46.2' },
    ]);
    expect(globals.Reactv16).toBe(React);
    expect(globals.FluentUIReactv940).toBe(Fluent);
    expect(globals.FluentUIReactv9).toBe(Fluent);
  });

  it('installs_nothing_for_a_control_without_platform_libraries', () => {
    installPlatformLibraries([]);
    expect(globals.Reactv16).toBeUndefined();
  });

  it('refuses_fluent_8_and_other_react_majors_without_installing_anything', () => {
    for (const lib of [
      { name: 'Fluent', version: '8.115.0' },
      { name: 'React', version: '17.0.2' },
      { name: 'Charts', version: '1.0.0' },
    ]) {
      expect(unsupportedPlatformLibrary([lib])).toEqual(lib);
      let error: unknown;
      try {
        installPlatformLibraries([{ name: 'React', version: '16.8.6' }, lib]);
      } catch (e) {
        error = e;
      }
      expect(error).toBeInstanceOf(PcfLoadError);
      expect((error as PcfLoadError).code).toBe('platformLibrary');
      expect((error as PcfLoadError).message).toContain(`${lib.name} ${lib.version}`);
      expect(globals.Reactv16).toBeUndefined();
    }
  });

  it('matches_names_case_insensitively', () => {
    expect(unsupportedPlatformLibrary([{ name: 'react', version: '16.0.0' }, { name: 'FLUENT', version: '9' }])).toBeUndefined();
  });
});
