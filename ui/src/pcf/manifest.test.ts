import { describe, expect, it } from 'vitest';
import { parseManifest } from './manifest';
import { PcfLoadError } from './errors';
import { standardControl } from '../test/pcf/controls';

// Shaped like the virtual field controls of a pac-built solution; written by hand.
const virtualDecimal = `<?xml version="1.0" encoding="utf-8"?>
<manifest>
  <control namespace="Acme" constructor="Gauge" version="1.0.1" display-name-key="Control_Name" description-key="Control_Description" control-type="virtual" api-version="1.3.18">
    <external-service-usage enabled="false" />
    <property name="value" display-name-key="Property_Value" of-type="Decimal" usage="bound" required="true" />
    <property name="caption" display-name-key="Property_Caption" of-type="SingleLine.Text" usage="input" required="false" />
    <resources>
      <code path="bundle.js" order="1" />
      <platform-library name="React" version="16.14.0" />
      <platform-library name="Fluent" version="9.46.2" />
      <resx path="strings/Gauge.1033.resx" version="1.0.0" />
      <resx path="strings/Gauge.1040.resx" version="1.0.0" />
    </resources>
    <feature-usage />
    <built-by name="pac" version="1.51.1" />
  </control>
</manifest>`;

const dataset = `<manifest><control namespace="Acme" constructor="Tree" version="2.0.0" control-type="standard">
  <data-set name="nodes" display-name-key="Nodes" />
  <property name="title" of-type="SingleLine.Text" usage="input" />
  <property name="numbers" of-type-group="numbers" usage="bound" />
  <type-group name="numbers"><type>Whole.None</type><type>Decimal</type></type-group>
  <resources><code path="b2.js" order="2" /><code path="b1.js" order="1" /><css path="a.css" order="1" /></resources>
  <feature-usage><uses-feature name="WebAPI" required="true" /><uses-feature name="Utility" required="false" /></feature-usage>
</control></manifest>`;

describe('parseManifest', () => {
  it('reads_a_virtual_bound_decimal_control', () => {
    expect(parseManifest(virtualDecimal)).toEqual({
      namespace: 'Acme',
      constructor: 'Gauge',
      version: '1.0.1',
      controlType: 'virtual',
      properties: [
        { name: 'value', ofType: 'Decimal', types: ['Decimal'], usage: 'bound', required: true, displayNameKey: 'Property_Value' },
        { name: 'caption', ofType: 'SingleLine.Text', types: ['SingleLine.Text'], usage: 'input', required: false, displayNameKey: 'Property_Caption' },
      ],
      dataSets: [],
      code: [{ path: 'bundle.js', order: 1 }],
      css: [],
      resx: [
        { path: 'strings/Gauge.1033.resx', lcid: 1033 },
        { path: 'strings/Gauge.1040.resx', lcid: 1040 },
      ],
      img: [],
      platformLibraries: [
        { name: 'React', version: '16.14.0' },
        { name: 'Fluent', version: '9.46.2' },
      ],
      features: [],
    });
  });

  it('reads_a_standard_control_with_css_img_and_default_value', () => {
    const m = parseManifest(standardControl.manifest);
    expect(m.controlType).toBe('standard');
    expect(m.css).toEqual([{ path: 'css/doubler.css', order: 1 }]);
    expect(m.img).toEqual(['img/icon.png']);
    expect(m.properties[1]).toEqual({
      name: 'factor', ofType: 'Whole.None', types: ['Whole.None'], usage: 'input', required: false, defaultValue: '2', displayNameKey: 'Factor_Name',
    });
  });

  it('reads display-name-key of a property', () => {
    const m = parseManifest(dataset.replace('<property name="title"', '<property name="title" display-name-key="Title_Key"'));
    expect(m.properties[0].displayNameKey).toBe('Title_Key');
    // Without the attribute there is no key.
    expect(m.properties[1]).not.toHaveProperty('displayNameKey');
  });

  it('reads_data_sets_features_type_groups_and_sorts_code_by_order', () => {
    const m = parseManifest(dataset);
    expect(m.dataSets).toEqual([{ name: 'nodes', hasPropertySet: false }]);
    expect(m.features).toEqual([
      { name: 'WebAPI', required: true },
      { name: 'Utility', required: false },
    ]);
    expect(m.code.map((c) => c.path)).toEqual(['b1.js', 'b2.js']);
    expect(m.properties[1]).toMatchObject({ name: 'numbers', ofType: '', types: ['Whole.None', 'Decimal'], usage: 'bound', required: false });
  });

  it('manifest_property_set_flag', () => {
    const m = parseManifest(`<manifest><control namespace="Acme" constructor="Grid" version="1.0.0" control-type="virtual">
  <data-set name="rows" display-name-key="Rows"><property-set name="amount" display-name-key="Amount" of-type="Decimal" usage="bound" required="true" /></data-set>
  <data-set name="plain" display-name-key="Plain" />
</control></manifest>`);
    expect(m.dataSets).toEqual([
      { name: 'rows', hasPropertySet: true },
      { name: 'plain', hasPropertySet: false },
    ]);
  });

  it('refuses_a_document_without_a_control', () => {
    expect(() => parseManifest('<manifest />')).toThrow(PcfLoadError);
    expect(() => parseManifest('<manifest')).toThrow(PcfLoadError);
    let error: unknown;
    try {
      parseManifest('<manifest><control namespace="A" /></manifest>');
    } catch (e) {
      error = e;
    }
    expect((error as PcfLoadError).code).toBe('manifest');
  });
});
