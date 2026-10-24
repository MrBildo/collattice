import '@testing-library/jest-dom/vitest';

// jsdom does not implement ResizeObserver. Shim it so any component that uses
// ResizeObserver (SearchCommand, use-lane-resize, use-label-layout, etc.) can
// render in tests without crashing. The no-op implementation is sufficient for
// unit tests that assert on rendered output rather than on resize behaviour.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
// eslint-disable-next-line @typescript-eslint/no-explicit-any
(globalThis as any).ResizeObserver = ResizeObserverStub;

// jsdom does not implement matchMedia. useIsMobile (gates all drag-drop)
// subscribes to a media query via useSyncExternalStore, so any component that
// mounts a drag surface needs this. Default `matches: false` = desktop, which is
// the default drag-enabled state — tests that need to assert the mobile path
// override window.matchMedia per-test.
if (!window.matchMedia) {
  window.matchMedia = (query: string): MediaQueryList =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    }) as MediaQueryList;
}
