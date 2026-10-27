import { useEffect, useRef } from 'react';

/**
 * Calls `onArrive` for items that appear after the first render for this `key` (a match or persona). What is already
 * there when the page loads, or when the key changes, is history, not news.
 */
export function useArrivals<T>(items: T[], id: (item: T) => string, key: string, onArrive: (item: T) => void) {
  const seen = useRef<{ key: string; ids: Set<string> }>();
  const handler = useRef(onArrive);
  handler.current = onArrive;
  useEffect(() => {
    if (seen.current?.key !== key) {
      seen.current = { key, ids: new Set(items.map(id)) };
      return;
    }
    for (const item of items) {
      const k = id(item);
      if (seen.current.ids.has(k)) continue;
      seen.current.ids.add(k);
      handler.current(item);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [items, key]);
}
