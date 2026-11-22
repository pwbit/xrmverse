/**
 * The table metadata the properties panel needs (spec §4.6): the workflow's table and the tables it reaches (through a
 * lookup, a created record, a lookup literal), read with the admin pages' readers (`loadEntity`, `loadEntities` of
 * `EntityDefinitions`). Each table is read once per signed-in API and kept for the session; a table that cannot be read is
 * remembered as such, and the panel then falls back to typing logical names.
 */
import { useEffect, useMemo, useState } from 'react';
import type { EntityMeta } from '../../admin/tables/metadata';
import { loadEntities, loadEntity } from '../../admin/tables/tableApi';
import type { XrmApi } from '../../shell/api/client';

/** What the panel's editors read: synchronous, undefined while a table loads or when it cannot be read. */
export interface MetadataSource {
  entity(name: string | undefined): EntityMeta | undefined;
  /** Every table (without columns), for a table picker. */
  tables(): EntityMeta[] | undefined;
}

/** No metadata at all: every picker falls back to text. */
export const NO_METADATA: MetadataSource = { entity: () => undefined, tables: () => undefined };

type Entry = EntityMeta | 'loading' | 'failed';

class MetadataCache {
  readonly entities = new Map<string, Entry>();
  tables: EntityMeta[] | 'loading' | 'failed' | undefined;
  private readonly listeners = new Set<() => void>();

  constructor(private readonly api: XrmApi) {}

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private notify(): void {
    for (const listener of this.listeners) listener();
  }

  entity(name: string): EntityMeta | undefined {
    const entry = this.entities.get(name);
    if (entry === undefined) {
      this.entities.set(name, 'loading');
      loadEntity(this.api, name).then(
        (meta) => {
          this.entities.set(name, meta);
          this.notify();
        },
        () => {
          this.entities.set(name, 'failed');
          this.notify();
        },
      );
      return undefined;
    }
    return typeof entry === 'object' ? entry : undefined;
  }

  allTables(): EntityMeta[] | undefined {
    if (this.tables === undefined) {
      this.tables = 'loading';
      loadEntities(this.api).then(
        (tables) => {
          this.tables = [...tables].sort((a, b) => a.logicalName.localeCompare(b.logicalName));
          this.notify();
        },
        () => {
          this.tables = 'failed';
          this.notify();
        },
      );
      return undefined;
    }
    return Array.isArray(this.tables) ? this.tables : undefined;
  }
}

const caches = new WeakMap<XrmApi, MetadataCache>();

/** The metadata of the signed-in API (none without one); a component using it re-renders when a table arrives. */
export function useStudioMetadata(api: XrmApi | undefined): MetadataSource {
  const cache = useMemo(() => {
    if (!api) return undefined;
    let found = caches.get(api);
    if (!found) {
      found = new MetadataCache(api);
      caches.set(api, found);
    }
    return found;
  }, [api]);
  const [version, setVersion] = useState(0);
  useEffect(() => cache?.subscribe(() => setVersion((v) => v + 1)), [cache]);
  return useMemo<MetadataSource>(
    () =>
      cache
        ? {
            entity: (name) => (name ? cache.entity(name) : undefined),
            tables: () => cache.allTables(),
          }
        : NO_METADATA,
    // A new source on every arrival, so that the editors that read it render again.
    [cache, version],
  );
}
