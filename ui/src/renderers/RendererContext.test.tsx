import { renderHook, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { classicRenderer } from './classic';
import type { Renderer } from './contract';
import { openAccounts } from './classic/viewTestSupport';
import { RendererProvider, useRenderer } from './RendererContext';

describe('RendererContext', () => {
  it('useRenderer returns classic outside a provider', () => {
    const { result } = renderHook(() => useRenderer());
    expect(result.current).toBe(classicRenderer);
    expect(result.current.id).toBe('classic');
  });

  it('useRenderer returns the provided renderer', () => {
    const test: Renderer = { ...classicRenderer, id: 'modern' };
    const { result } = renderHook(() => useRenderer(), { wrapper: ({ children }) => <RendererProvider renderer={test}>{children}</RendererProvider> });
    expect(result.current.id).toBe('modern');
  });

  it('pages draw through the provided renderer', async () => {
    const TestGrid = () => <div data-testid="test-grid" />;
    const test: Renderer = { ...classicRenderer, ViewGrid: TestGrid as unknown as Renderer['ViewGrid'] };
    openAccounts({ wrapper: ({ children }) => <RendererProvider renderer={test}>{children}</RendererProvider> });
    expect(await screen.findByTestId('test-grid')).toBeInTheDocument();
  });
});
