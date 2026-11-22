import { optionsOf } from '../admin/tables/fields/fieldProps';
import { sameValue } from '../admin/tables/formValues';
import { isNumeric, type AttributeMeta, type OptionMeta } from '../admin/tables/metadata';
import { collection, type Collection } from './collection';
import { AttributeFormat, AttributeType, REQUIRED_LEVELS, SUBMIT_MODES, type RequiredLevel, type SubmitMode } from './constants';
import type { Control } from './control';
import type { Runtime } from './formContext';
import { Stage, stub } from './notImplemented';
import { fromClientValue, toClientValue, type ClientValue } from './values';

export type Attribute = Record<string, unknown> & {
  getName(): string;
  getValue(): ClientValue;
  getIsDirty(): boolean;
  controls: Collection<Control>;
};

/** Learn's type string of an attribute kind (`getAttributeType`). */
export function attributeTypeOf(a: AttributeMeta): string {
  switch (a.kind) {
    case 'bool':
      return AttributeType.boolean;
    case 'int':
    case 'bigint':
      return AttributeType.integer;
    case 'choice':
      return AttributeType.optionset;
    case 'choices':
      return AttributeType.multiselectoptionset;
    case 'partylist':
      return AttributeType.lookup;
    case 'string':
    case 'memo':
    case 'decimal':
    case 'double':
    case 'money':
    case 'datetime':
    case 'lookup':
    case 'file':
    case 'image':
      return AttributeType[a.kind];
    default:
      // A column type Learn does not list (unsupported in UI-1b): no guessed string.
      throw new Error(`formContext.getAttribute("${a.logicalName}").getAttributeType is not available in this version for a ${a.typeName} column (stage after UI-1).`);
  }
}

/** The server's `Format` of a column as Learn's `getFormat` string; `null` when the form does not know it. */
const FORMATS: Record<string, string> = {
  dateonly: AttributeFormat.date,
  dateandtime: AttributeFormat.datetime,
  duration: AttributeFormat.duration,
  email: AttributeFormat.email,
  language: AttributeFormat.language,
  none: AttributeFormat.none,
  phone: AttributeFormat.phone,
  text: AttributeFormat.text,
  textarea: AttributeFormat.textarea,
  tickersymbol: AttributeFormat.tickersymbol,
  timezone: AttributeFormat.timezone,
  url: AttributeFormat.url,
};
function formatOf(a: AttributeMeta): string | null {
  if (a.kind === 'datetime') return a.dateOnly ? AttributeFormat.date : AttributeFormat.datetime;
  if (a.kind === 'bool' || !a.format) return null;
  return FORMATS[a.format.toLowerCase()] ?? null;
}

const fromRequirement = (level: string): RequiredLevel =>
  level === 'SystemRequired' || level === 'ApplicationRequired' ? 'required' : level === 'Recommended' ? 'recommended' : 'none';

const clientOption = (o: OptionMeta) => ({ value: o.value, text: o.label });

/** One column of the form (Learn, Columns): read and written through the form state and the overlay. */
export function createAttribute(rt: Runtime, meta: AttributeMeta): Attribute {
  const name = meta.logicalName;
  const latest = () => rt.env.state.latest();
  const options = () => optionsOf(meta, rt.env.ctx.fieldContext);
  // #131: User Local dates in the user's time zone (UTC on a page without the user's settings).
  const zone = () => rt.env.timeZone ?? 'Etc/UTC';
  const nav = (target: string) => rt.env.ctx.fieldContext.lookups.find((r) => r.referencingAttribute === name && r.referencedEntity === target)?.nav;
  const isChoice = meta.kind === 'choice' || meta.kind === 'choices';
  const selected = (): OptionMeta[] => {
    const v = toClientValue(meta, latest().values[name], zone());
    const values = Array.isArray(v) ? (v as number[]) : typeof v === 'number' ? [v] : [];
    return values.flatMap((x) => options().filter((o) => o.value === x));
  };

  const attribute: Attribute = {
    getName: () => name,
    getAttributeType: () => attributeTypeOf(meta),
    getFormat: () => formatOf(meta),
    getValue: () => toClientValue(meta, latest().values[name], zone()),
    // A script's setValue never raises OnChange (Learn); fireOnChange does.
    setValue: (value: unknown) => rt.env.state.setValue(name, fromClientValue(meta, value, nav, zone()), 'script'),
    getIsDirty: () => !sameValue(meta, latest().original[name], latest().values[name]),
    getRequiredLevel: () => rt.env.overlay.attribute(name).requiredLevel ?? fromRequirement(meta.requiredLevel),
    setRequiredLevel: (level: unknown) => {
      if (!REQUIRED_LEVELS.includes(level as RequiredLevel)) throw new Error(`setRequiredLevel on ${name}: "${String(level)}" is not a required level.`);
      rt.env.overlay.setAttribute(name, { requiredLevel: level as RequiredLevel });
    },
    getSubmitMode: () => rt.env.overlay.attribute(name).submitMode ?? 'dirty',
    setSubmitMode: (mode: unknown) => {
      if (!SUBMIT_MODES.includes(mode as SubmitMode)) throw new Error(`setSubmitMode on ${name}: "${String(mode)}" is not a submit mode.`);
      rt.env.overlay.setAttribute(name, { submitMode: mode as SubmitMode });
    },
    addOnChange: (fn: unknown) => {
      if (typeof fn !== 'function') return;
      const list = rt.onChange.get(name) ?? [];
      if (!list.includes(fn as never)) rt.onChange.set(name, [...list, fn as never]);
    },
    removeOnChange: (fn: unknown) => {
      const list = rt.onChange.get(name) ?? [];
      rt.onChange.set(name, list.filter((f) => f !== fn));
    },
    fireOnChange: () => rt.raiseChange(name),
    controls: collection(() => rt.controls().filter((c) => (c.getAttribute as (() => Attribute | null) | undefined)?.() === attribute)),
    getParent: () => rt.fc.data.entity,
    getUserPrivilege: stub(`formContext.getAttribute("${name}").getUserPrivilege`, Stage.later),
    isValid: stub(`formContext.getAttribute("${name}").isValid`, Stage.later),
    setIsValid: stub(`formContext.getAttribute("${name}").setIsValid`, Stage.later),
  };

  if (isChoice || meta.kind === 'bool') {
    attribute.getInitialValue = () => toClientValue(meta, latest().original[name], zone());
  }
  if (isChoice) {
    Object.assign(attribute, {
      getOptions: () => options().map(clientOption),
      getOption: (arg: unknown) => {
        const found = options().find((o) => (typeof arg === 'number' ? o.value === arg : o.label === arg));
        return found ? clientOption(found) : null;
      },
      getText: () => {
        const texts = selected().map((o) => o.label);
        return meta.kind === 'choices' ? (texts.length ? texts : null) : (texts[0] ?? null);
      },
      getSelectedOption: () => {
        const found = selected().map(clientOption);
        return meta.kind === 'choices' ? (found.length ? found : null) : (found[0] ?? null);
      },
    });
  }
  if (isNumeric(meta.kind)) {
    Object.assign(attribute, {
      getMax: () => meta.maxValue ?? null,
      getMin: () => meta.minValue ?? null,
      getPrecision: () => meta.precision ?? null,
      setPrecision: stub(`formContext.getAttribute("${name}").setPrecision`, Stage.later),
    });
  }
  if (meta.kind === 'string') attribute.getMaxLength = () => meta.maxLength ?? null;
  if (meta.kind === 'lookup' || meta.kind === 'partylist') attribute.getIsPartyList = () => meta.kind === 'partylist';
  return attribute;
}
