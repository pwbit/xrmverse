import type { LookupRelationship } from '../tableApi';
import type { FieldValue } from '../formValues';
import type { AttributeMeta, OptionMeta } from '../metadata';

/** What the form knows that an editor may need besides its own value. */
export interface FieldContext {
  table: string;
  /** The lookups of the table (its many-to-one relationships). */
  lookups: LookupRelationship[];
  /** Options of global choices by name; empty until loaded. */
  globalChoices: Record<string, OptionMeta[]>;
  /**
   * The record being edited, as loaded; undefined on the create form. File and image editors upload to it and read their
   * own column from it.
   */
  record?: { set: string; id: string; data: Record<string, unknown> };
  /** The navigation property of the record's party rows (`{activity}_activity_parties`), when the table has party lists. */
  partyNavigation?: string;
}

export interface FieldProps {
  attr: AttributeMeta;
  value: FieldValue | undefined;
  onChange: (value: FieldValue) => void;
  /** The mode does not allow writing this field (create vs update), or the form is busy. */
  disabled: boolean;
  /** A message for this field, e.g. a number that does not parse. */
  error?: string;
  context: FieldContext;
  /**
   * Lookups only, optional: called before each search of `target`; returns extra FetchXML `<filter>` elements that the search
   * ANDs with its own (a form script's PreSearch handlers). Without it the search is the plain name search.
   */
  preSearch?: (target: string) => string[];
  /** Lookups only, optional: the tables the lookup offers, when a form script narrowed them. */
  entityTypes?: string[];
}

/** The options an attribute offers: its own, else those of its global choice. */
export const optionsOf = (attr: AttributeMeta, context: FieldContext): OptionMeta[] =>
  attr.options.length > 0 ? attr.options : (attr.globalOptionSet ? (context.globalChoices[attr.globalOptionSet] ?? []) : []);
