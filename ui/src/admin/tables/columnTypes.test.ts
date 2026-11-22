import { describe, expect, it } from 'vitest';
import fixture from '../../../../fixtures/ui/column-types.json';
import { columnTypes, maxSizeInKB } from './columnTypes';

describe('columnTypes', () => {
  it('Column_types_match_the_dotnet_list', () => {
    // The fixture is committed and asserted against SchemaColumnTypes.Names by tests/Xrmd.Schema.Tests/UiFixtureTests.cs.
    expect([...columnTypes]).toEqual(fixture.names);
  });

  it('File_and_image_size_limits_match_the_dotnet_values', () => {
    // The fixture's limits are asserted against Xrmd.Metadata.FileColumns by tests/Xrmd.Schema.Tests/UiFixtureTests.cs.
    expect(maxSizeInKB).toEqual(fixture.maxSizeInKB);
  });
});
