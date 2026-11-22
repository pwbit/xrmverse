import type { RibbonAction } from '../model/ribbon';
import { parameterValues, type RibbonEnv } from './rules';

/**
 * Runs the `JavaScriptFunction`s of a command one after another (ruling E4), each awaited. Rejects with the first error;
 * the actions after it do not run. Showing the error is the caller's job.
 */
export async function runActions(actions: RibbonAction[], env: RibbonEnv): Promise<void> {
  for (const action of actions) {
    const fn = await env.resolve(action.library, action.functionName);
    await fn(...parameterValues(action.parameters, env));
  }
}
