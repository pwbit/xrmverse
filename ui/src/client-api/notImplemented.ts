/** Stages named by the "not available" errors (spec §5.4). */
export const Stage = {
  /** Declared out of UI-1: business process flows, knowledge search, mobile-only members. */
  later: 'after UI-1',
} as const;

const message = (path: string, stage: string) => `${path} is not available in this version (stage ${stage}).`;

/** The "not available" error of `path`, for a member that fails through a rejected promise instead of a throw. */
export const notAvailable = (path: string, stage: string): Error => new Error(message(path, stage));

/**
 * A documented Client API member that this version does not implement: calling it throws an error that names the member as
 * a script writes it (`formContext.data.process`, `Xrm.Panel`) and the stage that brings it. It is never `undefined` and
 * never a silent no-op.
 */
export function stub(path: string, stage: string): (...args: unknown[]) => never {
  return () => {
    throw new Error(message(path, stage));
  };
}

/** Defines `name` on `target` as a documented property that throws the same error when it is read. */
export function stubProperty(target: object, name: string, path: string, stage: string): void {
  Object.defineProperty(target, name, {
    enumerable: false,
    get: () => {
      throw new Error(message(path, stage));
    },
  });
}

/** A documented `Xrm` member UI-1c leaves out: a method throws when called, a property or namespace when it is read. */
export interface NotImplemented {
  path: string;
  stage: string;
  kind: 'method' | 'property';
}

const GLOBAL = 'Xrm.Utility.getGlobalContext()';
const later = (kind: NotImplemented['kind'], ...paths: string[]): NotImplemented[] => paths.map((path) => ({ path, stage: Stage.later, kind }));

/**
 * The documented members under `Xrm` (Learn, Client API Xrm object and its namespaces) that UI-1c does not implement, by the
 * path a script writes. Ruling C10: `Xrm.App`, `Xrm.Device`, `Xrm.Encoding` and `Xrm.Panel` throw on use; `Xrm.Copilot` with them.
 */
export const XRM_NOT_IMPLEMENTED: readonly NotImplemented[] = [
  ...later('property', 'Xrm.App', 'Xrm.Copilot', 'Xrm.Device', 'Xrm.Encoding', 'Xrm.Panel'),
  ...later('property', 'Xrm.WebApi.offline'),
  ...later('method', 'Xrm.Navigation.openFile'),
  ...later(
    'method',
    'Xrm.Utility.getAllowedStatusTransitions',
    'Xrm.Utility.getEntityMainFormDescriptor',
    'Xrm.Utility.getLearningPathAttributeName',
    'Xrm.Utility.getPageContext',
    'Xrm.Utility.getResourceString',
    'Xrm.Utility.invokeProcessAction',
  ),
  ...later('method', 'Xrm.Utility.refreshParentGrid'),
  ...later('property', `${GLOBAL}.client`),
  ...later('method', `${GLOBAL}.getAdvancedConfigSetting`, `${GLOBAL}.getWebResourceUrl`, `${GLOBAL}.isOnPremises`),
  ...later(
    'property',
    `${GLOBAL}.userSettings.defaultDashboardId`,
    `${GLOBAL}.userSettings.isGuidedHelpEnabled`,
    `${GLOBAL}.userSettings.isHighContrastEnabled`,
    `${GLOBAL}.userSettings.securityRolePrivileges`,
    `${GLOBAL}.userSettings.transactionCurrency`,
    `${GLOBAL}.userSettings.transactionCurrencyId`,
  ),
  ...later('method', `${GLOBAL}.userSettings.getSecurityRolePrivilegesInfo`),
  // #131 (ruling Z7): the patterns usersettings holds no column for.
  ...later(
    'property',
    ...['LongDatePattern', 'LongTimePattern', 'FullDateTimePattern', 'MonthDayPattern', 'YearMonthPattern', 'ShortestDayNames'].map(
      (name) => `${GLOBAL}.userSettings.dateFormattingInfo.${name}`,
    ),
  ),
  ...later(
    'property',
    `${GLOBAL}.organizationSettings.attributes`,
    `${GLOBAL}.organizationSettings.baseCurrency`,
    `${GLOBAL}.organizationSettings.isTrialOrganization`,
    `${GLOBAL}.organizationSettings.organizationExpiryDate`,
    `${GLOBAL}.organizationSettings.useSkypeProtocol`,
    `${GLOBAL}.organizationSettings.fullNameConventionCode`,
  ),
];

/**
 * Puts on `target` the entries of {@link XRM_NOT_IMPLEMENTED} whose path is `owner` plus one member name (`owner` is
 * `Xrm.Utility`, `Xrm.Utility.getGlobalContext().userSettings`, ...). They are not enumerable: they are documented, not data.
 */
export function stubMembers(target: object, owner: string): void {
  for (const entry of XRM_NOT_IMPLEMENTED) {
    if (!entry.path.startsWith(`${owner}.`)) continue;
    const name = entry.path.slice(owner.length + 1);
    if (name.includes('.')) continue;
    if (entry.kind === 'method') Object.defineProperty(target, name, { enumerable: false, value: stub(entry.path, entry.stage) });
    else stubProperty(target, name, entry.path, entry.stage);
  }
}
