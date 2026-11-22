/** One condition of a ribbon `DisplayRule` or `EnableRule` (UI-1e ruling E3). */
export type RibbonRule =
  | { kind: 'formState'; state: string; invert: boolean; dflt: boolean }
  | { kind: 'value'; field: string; value: string; invert: boolean; dflt: boolean }
  | { kind: 'or'; groups: RibbonRule[][]; invert: boolean }
  | { kind: 'entity'; entity: string; context?: string; invert: boolean }
  | { kind: 'clientType'; type: string; invert: boolean }
  | { kind: 'custom'; library: string; functionName: string; parameters: RibbonParameter[]; invert: boolean; dflt: boolean };

/** A parameter of a `JavaScriptFunction` or `CustomRule`, in document order (ruling E4). */
export type RibbonParameter = { kind: 'crm'; value: string } | { kind: 'string' | 'bool' | 'int' | 'decimal'; value: string };

export interface RibbonAction {
  /** The web resource name, without the `$webresource:` prefix. */
  library: string;
  functionName: string;
  parameters: RibbonParameter[];
}

export interface RibbonCommand {
  /** The `CustomAction` Id. */
  id: string;
  buttonId: string;
  sequence: number;
  label: string;
  tooltip?: string;
  /** `ModernImage` */
  icon?: string;
  displayRules: RibbonRule[];
  enableRules: RibbonRule[];
  actions: RibbonAction[];
  /** Why the host cannot honour the command (plain English); the command is then not shown. */
  unavailable?: string;
}
