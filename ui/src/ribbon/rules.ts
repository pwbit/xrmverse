import type { RibbonParameter, RibbonRule } from '../model/ribbon';

/** What a ribbon rule or action reads from the open form (UI-1e rulings E3 and E4). */
export interface RibbonEnv {
  formContext: unknown;
  /** The table logical name. */
  entity: string;
  recordId?: string;
  formId: string;
  formType: 'create' | 'existing';
  /** The form is read-only by rights. */
  readOnly: boolean;
  /** Every control is disabled. */
  allDisabled: boolean;
  /** The attribute's current value as the text a `ValueRule` compares with: `true`/`false`, a choice's number, `null`. */
  valueOf(field: string): { onForm: boolean; text: string };
  lcid: number;
  orgLcid: number;
  orgName: string;
  /** The function `fn` of the web resource `library`, loaded in the form's ribbon script host. */
  resolve(library: string, fn: string): Promise<Function>;
}

/** A `CrmParameter` value the host does not know; the command that uses it is not available. */
export class UnsupportedRibbonParameter extends Error {
  constructor(readonly parameter: string) {
    super(`CrmParameter '${parameter}' is not supported in this version`);
    this.name = 'UnsupportedRibbonParameter';
  }
}

/** A `CustomRule` that has not answered after this long counts as unable to be evaluated. */
export const CUSTOM_RULE_TIMEOUT_MS = 10_000;

const braced = (id: string): string => `{${id.replace(/^\{|\}$/g, '').toUpperCase()}}`;

function typed(p: RibbonParameter): unknown {
  const text = p.value.trim();
  switch (p.kind) {
    case 'string':
      return p.value;
    case 'bool':
      return text.toLowerCase() === 'true';
    case 'int':
      if (!/^[+-]?\d+$/.test(text)) throw new Error(`IntParameter value '${p.value}' is not an integer`);
      return Number(text);
    case 'decimal':
      if (text === '' || !Number.isFinite(Number(text))) throw new Error(`DecimalParameter value '${p.value}' is not a number`);
      return Number(text);
    default:
      return undefined;
  }
}

/** The arguments of a `CustomRule` or `JavaScriptFunction`, in document order (ruling E4). */
export function parameterValues(params: RibbonParameter[], env: RibbonEnv): unknown[] {
  return params.map((p) => {
    if (p.kind !== 'crm') return typed(p);
    switch (p.value) {
      case 'PrimaryControl':
        return env.formContext;
      case 'PrimaryEntityTypeName':
        return env.entity;
      case 'FirstPrimaryItemId':
        return env.recordId ? braced(env.recordId) : '';
      case 'PrimaryItemIds':
        return env.recordId ? [braced(env.recordId)] : [];
      case 'PrimaryControlId':
        return env.formId;
      case 'UserLcid':
        return env.lcid;
      case 'OrgLcid':
        return env.orgLcid;
      case 'OrgName':
        return env.orgName;
      default:
        throw new UnsupportedRibbonParameter(p.value);
    }
  });
}

/** A `CustomRule` that did not settle in time: its final answer is false (Learn), with no `Default` and no inversion. */
class CustomRuleTimeout extends Error {}

/** Resolves with the rule's answer; rejects when it cannot be evaluated (the caller then uses `Default`). */
async function custom(rule: Extract<RibbonRule, { kind: 'custom' }>, env: RibbonEnv, signal?: AbortSignal): Promise<boolean> {
  const fn = await env.resolve(rule.library, rule.functionName);
  const args = parameterValues(rule.parameters, env);
  let timer: ReturnType<typeof setTimeout> | undefined;
  let onAbort: (() => void) | undefined;
  const limit = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new CustomRuleTimeout()), CUSTOM_RULE_TIMEOUT_MS);
    // An abort frees the evaluation at once; the caller discards its answer.
    onAbort = () => reject(new CustomRuleTimeout());
    if (signal?.aborted) onAbort();
    else signal?.addEventListener('abort', onAbort, { once: true });
  });
  try {
    return (await Promise.race([Promise.resolve(fn(...args)), limit])) === true;
  } finally {
    clearTimeout(timer);
    if (onAbort) signal?.removeEventListener('abort', onAbort);
  }
}

async function one(rule: RibbonRule, env: RibbonEnv, signal?: AbortSignal): Promise<boolean> {
  let result: boolean;
  switch (rule.kind) {
    case 'formState':
      result =
        rule.state === 'Create' ? env.formType === 'create'
        : rule.state === 'Existing' ? env.formType === 'existing'
        : rule.state === 'ReadOnly' ? env.readOnly
        : rule.state === 'Disabled' ? env.allDisabled
        : rule.state === 'BulkEdit' ? false
        : rule.dflt;
      break;
    case 'value': {
      const v = env.valueOf(rule.field);
      result = v.onForm ? v.text === rule.value : rule.dflt;
      break;
    }
    case 'or': {
      result = false;
      for (const group of rule.groups) {
        if (await all(group, env, signal)) {
          result = true;
          break;
        }
      }
      break;
    }
    case 'entity':
      // Any Context other than Form or HomePageGrid cannot be evaluated: false (an EntityRule has no Default).
      result = rule.context === undefined || rule.context === 'Form' ? rule.entity === env.entity : false;
      break;
    case 'clientType':
      result = rule.type === 'Web';
      break;
    case 'custom':
      try {
        result = await custom(rule, env, signal);
      } catch (e) {
        if (e instanceof CustomRuleTimeout) return false;
        result = rule.dflt;
      }
      break;
  }
  return rule.invert ? !result : result;
}

async function all(rules: RibbonRule[], env: RibbonEnv, signal?: AbortSignal): Promise<boolean> {
  for (const rule of rules) {
    if (signal?.aborted) return false;
    const ok = await one(rule, env, signal);
    if (signal?.aborted || !ok) return false;
  }
  return true;
}

/**
 * Whether every rule holds (ruling E3: a definition is an AND of its rules; `OrRule` is an OR of ANDs). `InvertResult`
 * applies to the result, `Default` included. A `CustomRule` that throws gives its `Default`; one that does not settle in
 * 10 seconds is false, final (no `Default`, no inversion). An aborted evaluation answers false (its caller discards it).
 */
export function evaluate(rules: RibbonRule[], env: RibbonEnv, signal?: AbortSignal): Promise<boolean> {
  return all(rules, env, signal);
}
