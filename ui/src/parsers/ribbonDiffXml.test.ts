import { describe, expect, it } from 'vitest';
import { formCommands } from './ribbonDiffXml';

const LOC = 'Mscrm.Form.xv_order.MainTab.Save.Controls._children';

/** The Appendix A shape: one button with an Or enable rule, a display rule and a library call. */
const SEND = `<RibbonDiffXml>
  <CustomActions>
    <CustomAction Id="xv.order.Send.CustomAction" Location="${LOC}" Sequence="5">
      <CommandUIDefinition>
        <Button Id="xv.order.Send.Button" Command="xv.order.Send.Command" LabelText="$LocLabels:xv.order.Send.Label"
                ToolTipTitle="$LocLabels:xv.order.Send.Label" ToolTipDescription="$LocLabels:xv.order.Send.Tip" TemplateAlias="o1" ModernImage="Send" />
      </CommandUIDefinition>
    </CustomAction>
  </CustomActions>
  <Templates><RibbonTemplates Id="Mscrm.Templates"></RibbonTemplates></Templates>
  <CommandDefinitions>
    <CommandDefinition Id="xv.order.Send.Command">
      <EnableRules><EnableRule Id="xv.order.Send.Enable" /></EnableRules>
      <DisplayRules><DisplayRule Id="xv.order.Existing" /></DisplayRules>
      <Actions>
        <JavaScriptFunction Library="$webresource:xv_/scripts/order.js" FunctionName="XV.Order.send">
          <CrmParameter Value="PrimaryControl" />
        </JavaScriptFunction>
      </Actions>
    </CommandDefinition>
  </CommandDefinitions>
  <RuleDefinitions>
    <TabDisplayRules />
    <DisplayRules>
      <DisplayRule Id="xv.order.Existing"><FormStateRule State="Create" InvertResult="true" /></DisplayRule>
    </DisplayRules>
    <EnableRules>
      <EnableRule Id="xv.order.Send.Enable">
        <OrRule>
          <Or><ValueRule Field="statuscode" Value="1" /></Or>
          <Or><ValueRule Field="statuscode" Value="100000000" /></Or>
        </OrRule>
      </EnableRule>
    </EnableRules>
  </RuleDefinitions>
  <LocLabels>
    <LocLabel Id="xv.order.Send.Label"><Titles><Title description="Send" languagecode="1033" /><Title description="Invia" languagecode="1040" /></Titles></LocLabel>
    <LocLabel Id="xv.order.Send.Tip"><Titles><Title description="Send the order" languagecode="1033" /></Titles></LocLabel>
  </LocLabels>
</RibbonDiffXml>`;

interface Spec {
  id: string;
  location?: string;
  sequence?: number;
  label?: string;
  rules?: string;
  action?: string;
  locLabel?: string;
}

/** A small diff with one button per spec. Each button's command uses the display rule `show` unless `rules` overrides it. */
function diff(specs: Spec[], extra = ''): string {
  const actions = specs
    .map(
      (s) => `<CustomAction Id="${s.id}.CA" Location="${s.location ?? LOC}"${s.sequence === undefined ? '' : ` Sequence="${s.sequence}"`}>
        <CommandUIDefinition><Button Id="${s.id}.B" Command="${s.id}.C" LabelText="${s.label ?? s.id}" /></CommandUIDefinition></CustomAction>`,
    )
    .join('');
  const commands = specs
    .map(
      (s) => `<CommandDefinition Id="${s.id}.C">
        <DisplayRules><DisplayRule Id="show" /></DisplayRules>
        <Actions>${s.action ?? `<JavaScriptFunction Library="$webresource:xv_/a.js" FunctionName="XV.${s.id}" />`}</Actions>
      </CommandDefinition>`,
    )
    .join('');
  return `<CustomActions>${actions}${extra}</CustomActions>
    <CommandDefinitions>${commands}</CommandDefinitions>
    <RuleDefinitions><DisplayRules><DisplayRule Id="show"><FormStateRule State="Existing" /></DisplayRule></DisplayRules></RuleDefinitions>`;
}

const labelsXml = (id: string, titles: string) => `<LocLabels><LocLabel Id="${id}"><Titles>${titles}</Titles></LocLabel></LocLabels>`;

describe('formCommands', () => {
  it('parses_the_op365_rfq_send_shape', () => {
    const { commands, warnings } = formCommands('xv_order', [SEND], 1033);
    expect(warnings).toEqual([]);
    expect(commands).toEqual([
      {
        id: 'xv.order.Send.CustomAction',
        buttonId: 'xv.order.Send.Button',
        sequence: 5,
        label: 'Send',
        tooltip: 'Send\nSend the order',
        icon: 'Send',
        displayRules: [{ kind: 'formState', state: 'Create', invert: true, dflt: false }],
        enableRules: [
          {
            kind: 'or',
            invert: false,
            groups: [
              [{ kind: 'value', field: 'statuscode', value: '1', invert: false, dflt: false }],
              [{ kind: 'value', field: 'statuscode', value: '100000000', invert: false, dflt: false }],
            ],
          },
        ],
        actions: [{ library: 'xv_/scripts/order.js', functionName: 'XV.Order.send', parameters: [{ kind: 'crm', value: 'PrimaryControl' }] }],
      },
    ]);
  });

  it('reads_a_diff_given_as_inner_xml', () => {
    const inner = SEND.replace(/^<RibbonDiffXml>/, '').replace(/<\/RibbonDiffXml>$/, '');
    expect(formCommands('xv_order', [inner], 1033).commands).toEqual(formCommands('xv_order', [SEND], 1033).commands);
  });

  it('later_diff_replaces_same_ids', () => {
    const first = diff([{ id: 'a', label: 'Old' }, { id: 'b' }]);
    const second = diff([{ id: 'a', label: 'New' }]);
    const { commands } = formCommands('xv_order', [first, second], 1033);
    expect(commands.map((c) => [c.id, c.label])).toEqual([
      ['a.CA', 'New'],
      ['b.CA', 'b'],
    ]);
  });

  it('later_diff_replaces_rule_and_loclabel_with_the_same_id', () => {
    const first = `${diff([{ id: 'a', label: '$LocLabels:l' }])}${labelsXml('l', '<Title description="Old" languagecode="1033" />')}`;
    const second = `<RuleDefinitions><DisplayRules><DisplayRule Id="show"><FormStateRule State="Create" /></DisplayRule></DisplayRules></RuleDefinitions>
      ${labelsXml('l', '<Title description="New" languagecode="1033" />')}`;
    const [c] = formCommands('xv_order', [first, second], 1033).commands;
    expect(c.label).toBe('New');
    expect(c.displayRules).toEqual([{ kind: 'formState', state: 'Create', invert: false, dflt: false }]);
  });

  it('grid_locations_are_ignored', () => {
    const xml = diff([
      { id: 'form' },
      { id: 'grid', location: 'Mscrm.HomepageGrid.xv_order.MainTab.Actions.Controls._children' },
      { id: 'sub', location: 'Mscrm.SubGrid.xv_order.MainTab.Actions.Controls._children' },
      { id: 'other', location: 'Mscrm.Form.xv_other.MainTab.Save.Controls._children' },
    ]);
    const { commands, warnings } = formCommands('xv_order', [xml], 1033);
    expect(commands.map((c) => c.id)).toEqual(['form.CA']);
    expect(warnings).toEqual([]);
  });

  it('hide_custom_action_is_a_warning', () => {
    const hide = `<HideCustomAction HideActionId="Mscrm.Form.xv_order.Delete" Location="Mscrm.Form.xv_order.MainTab.Actions.Delete" />
      <HideCustomAction HideActionId="g" Location="Mscrm.HomepageGrid.xv_order.Delete" />`;
    const { commands, warnings } = formCommands('xv_order', [diff([{ id: 'a' }], hide)], 1033);
    expect(commands).toHaveLength(1);
    expect(warnings).toEqual(["Hiding 'Mscrm.Form.xv_order.MainTab.Actions.Delete' is not supported in this version"]);
  });

  it('unknown_rule_marks_command_unavailable', () => {
    const xml = `${diff([{ id: 'a' }])}
      <RuleDefinitions><DisplayRules><DisplayRule Id="show"><SelectionCountRule Minimum="1" /></DisplayRule></DisplayRules></RuleDefinitions>`;
    const [c] = formCommands('xv_order', [xml], 1033).commands;
    expect(c.unavailable).toBe("rule 'SelectionCountRule' is not supported in this version");
  });

  it('unknown_form_state_marks_command_unavailable', () => {
    const rule = (state: string) =>
      `${diff([{ id: 'a' }])}<RuleDefinitions><DisplayRules><DisplayRule Id="show"><FormStateRule State="${state}" /></DisplayRule></DisplayRules></RuleDefinitions>`;
    expect(formCommands('xv_order', [rule('Bogus')], 1033).commands[0].unavailable).toBe("form state 'Bogus' is not supported in this version");
    for (const ok of ['Create', 'Existing', 'ReadOnly', 'Disabled', 'BulkEdit']) {
      expect(formCommands('xv_order', [rule(ok)], 1033).commands[0].unavailable).toBeUndefined();
    }
  });

  it('empty_sequence_sorts_last', () => {
    const xml = diff([{ id: 'a', sequence: 3 }]).replace('<CustomActions>', '<CustomActions><CustomAction Id="e.CA" Location="Mscrm.Form.xv_order.MainTab.Actions" Sequence=""><CommandUIDefinition><Button Id="e.B" Command="a.C" LabelText="e" /></CommandUIDefinition></CustomAction>');
    expect(formCommands('xv_order', [xml], 1033).commands.map((c) => c.id)).toEqual(['a.CA', 'e.CA']);
  });

  it('url_action_marks_command_unavailable', () => {
    const xml = diff([{ id: 'a', action: '<Url Address="https://example.invalid/" />' }]);
    const [c] = formCommands('xv_order', [xml], 1033).commands;
    expect(c.unavailable).toBe('Url actions are not supported in this version');
  });

  it('unknown_parameter_marks_command_unavailable', () => {
    const xml = diff([{ id: 'a', action: '<JavaScriptFunction Library="$webresource:xv_/a.js" FunctionName="f"><SelectedItemsParameter /></JavaScriptFunction>' }]);
    expect(formCommands('xv_order', [xml], 1033).commands[0].unavailable).toBe("parameter 'SelectedItemsParameter' is not supported in this version");
  });

  it('typed_parameters_keep_document_order', () => {
    const xml = diff([
      {
        id: 'a',
        action:
          '<JavaScriptFunction Library="$webresource:xv_/a.js" FunctionName="f"><StringParameter Value="s" /><BoolParameter Value="true" /><IntParameter Value="3" /><DecimalParameter Value="1.5" /><CrmParameter Value="FirstPrimaryItemId" /></JavaScriptFunction>',
      },
    ]);
    expect(formCommands('xv_order', [xml], 1033).commands[0].actions[0].parameters).toEqual([
      { kind: 'string', value: 's' },
      { kind: 'bool', value: 'true' },
      { kind: 'int', value: '3' },
      { kind: 'decimal', value: '1.5' },
      { kind: 'crm', value: 'FirstPrimaryItemId' },
    ]);
  });

  it('library_without_webresource_prefix_is_unavailable', () => {
    const xml = diff([{ id: 'a', action: '<JavaScriptFunction Library="/_static/a.js" FunctionName="f" />' }]);
    expect(formCommands('xv_order', [xml], 1033).commands[0].unavailable).toContain('/_static/a.js');
  });

  it('supported_rules_are_parsed_with_flags', () => {
    const xml = `${diff([{ id: 'a' }])}
      <RuleDefinitions><DisplayRules><DisplayRule Id="show">
        <EntityRule EntityName="xv_order" Context="Form" InvertResult="true" />
        <CrmClientTypeRule Type="Web" />
        <ValueRule Field="f" Value="x" Default="true" />
        <CustomRule Library="$webresource:xv_/r.js" FunctionName="XV.ok" Default="true"><StringParameter Value="p" /></CustomRule>
      </DisplayRule></DisplayRules></RuleDefinitions>`;
    expect(formCommands('xv_order', [xml], 1033).commands[0].displayRules).toEqual([
      { kind: 'entity', entity: 'xv_order', context: 'Form', invert: true },
      { kind: 'clientType', type: 'Web', invert: false },
      { kind: 'value', field: 'f', value: 'x', invert: false, dflt: true },
      { kind: 'custom', library: 'xv_/r.js', functionName: 'XV.ok', parameters: [{ kind: 'string', value: 'p' }], invert: false, dflt: true },
    ]);
  });

  it('undefined_and_platform_rules_make_the_command_unavailable', () => {
    const missing = `<CustomActions><CustomAction Id="a.CA" Location="${LOC}"><CommandUIDefinition><Button Id="a.B" Command="a.C" LabelText="A" /></CommandUIDefinition></CustomAction></CustomActions>
      <CommandDefinitions><CommandDefinition Id="a.C"><EnableRules><EnableRule Id="xv.nope" /></EnableRules></CommandDefinition></CommandDefinitions>`;
    expect(formCommands('xv_order', [missing], 1033).commands[0].unavailable).toBe("rule 'xv.nope' is not defined");
    const platform = missing.replace('xv.nope', 'Mscrm.CanWritePrimary');
    expect(formCommands('xv_order', [platform], 1033).commands[0].unavailable).toBe(
      "platform rule 'Mscrm.CanWritePrimary' is not available in this version",
    );
  });

  it('loclabel_falls_back_to_1033_then_first', () => {
    const make = (loc: string) => `${diff([{ id: 'a', label: '$LocLabels:l' }])}${labelsXml('l', loc)}`;
    const it1040 = '<Title description="Invia" languagecode="1040" /><Title description="Send" languagecode="1033" />';
    expect(formCommands('xv_order', [make(it1040)], 1040).commands[0].label).toBe('Invia');
    expect(formCommands('xv_order', [make(it1040)], 1031).commands[0].label).toBe('Send');
    const noEnglish = '<Title description="Zweite" languagecode="1031" /><Title description="Invia" languagecode="1040" />';
    expect(formCommands('xv_order', [make(noEnglish)], 1036).commands[0].label).toBe('Zweite');
  });

  it('plain_label_text_is_used_as_is', () => {
    expect(formCommands('xv_order', [diff([{ id: 'a', label: 'Plain text' }])], 1040).commands[0].label).toBe('Plain text');
  });

  it('missing_loclabel_shows_id', () => {
    expect(formCommands('xv_order', [diff([{ id: 'a', label: '$LocLabels:xv.gone' }])], 1033).commands[0].label).toBe('xv.gone');
  });

  it('sequence_then_document_order', () => {
    const xml = diff([{ id: 'c', sequence: 9 }, { id: 'none' }, { id: 'a', sequence: 1 }, { id: 'b', sequence: 9 }]);
    expect(formCommands('xv_order', [xml], 1033).commands.map((c) => c.id)).toEqual(['a.CA', 'c.CA', 'b.CA', 'none.CA']);
  });

  it('empty_skeleton_and_malformed_diffs', () => {
    const { commands, warnings } = formCommands('xv_order', ['<CustomActions /><CommandDefinitions />', '<oops'], 1033);
    expect(commands).toEqual([]);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain('could not be read');
  });
});
