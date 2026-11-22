import { createContext } from 'react';

/**
 * The IANA time zone the forms below show and take User Local dates in (#131, ruling Z6): the signed-in user's, provided by
 * `UserSettingsGate` in the apps. Absent (the web admin), dates stay in UTC.
 */
export const DisplayZoneContext = createContext<string | undefined>(undefined);
