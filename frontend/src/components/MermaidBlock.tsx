import { useEffect, useId, useRef, useState } from 'react';
import mermaid from 'mermaid';

let mermaidInitialized = false;

function ensureMermaidInitialized() {
  if (!mermaidInitialized) {
    mermaid.initialize({
      startOnLoad: false,
      theme: 'base',
      themeVariables: {
        // Background — match code block bg
        background: 'hsl(222, 18%, 15%)',
        mainBkg: 'hsl(222, 18%, 15%)',

        // Primary nodes — Collattice cyan
        primaryColor: 'hsl(195, 90%, 20%)',
        primaryTextColor: 'hsl(195, 90%, 85%)',
        primaryBorderColor: 'hsl(195, 90%, 55%)',

        // Secondary nodes — Collattice amber/accent
        secondaryColor: 'hsl(36, 60%, 18%)',
        secondaryTextColor: 'hsl(36, 100%, 75%)',
        secondaryBorderColor: 'hsl(36, 100%, 55%)',

        // Tertiary
        tertiaryColor: 'hsl(222, 15%, 22%)',
        tertiaryTextColor: 'hsl(210, 15%, 80%)',
        tertiaryBorderColor: 'hsl(222, 12%, 35%)',

        // Text
        textColor: 'hsl(210, 15%, 90%)',
        nodeTextColor: 'hsl(210, 15%, 90%)',

        // Lines & edges
        lineColor: 'hsl(195, 60%, 50%)',
        edgeLabelBackground: 'hsl(222, 18%, 15%)',

        // Notes
        noteBkgColor: 'hsl(36, 60%, 18%)',
        noteTextColor: 'hsl(36, 100%, 80%)',
        noteBorderColor: 'hsl(36, 100%, 55%)',

        // Sequence diagram
        actorBkg: 'hsl(195, 90%, 20%)',
        actorTextColor: 'hsl(195, 90%, 85%)',
        actorBorder: 'hsl(195, 90%, 55%)',
        activationBkgColor: 'hsl(195, 50%, 25%)',
        activationBorderColor: 'hsl(195, 90%, 55%)',
        signalColor: 'hsl(210, 15%, 80%)',
        signalTextColor: 'hsl(210, 15%, 90%)',
        labelBoxBkgColor: 'hsl(222, 18%, 15%)',
        labelBoxBorderColor: 'hsl(195, 90%, 55%)',
        labelTextColor: 'hsl(210, 15%, 90%)',
        loopTextColor: 'hsl(210, 15%, 90%)',

        // Pie chart
        pie1: 'hsl(195, 90%, 55%)',
        pie2: 'hsl(36, 100%, 55%)',
        pie3: 'hsl(150, 60%, 40%)',
        pie4: 'hsl(280, 60%, 55%)',
        pie5: 'hsl(0, 72%, 55%)',
        pie6: 'hsl(195, 60%, 35%)',
        pie7: 'hsl(36, 70%, 40%)',
        pieStrokeColor: 'hsl(222, 12%, 25%)',
        pieTitleTextColor: 'hsl(210, 15%, 90%)',
        pieSectionTextColor: 'hsl(0, 0%, 100%)',
        pieLegendTextColor: 'hsl(210, 15%, 80%)',
        pieStrokeWidth: '1px',
        pieOuterStrokeColor: 'hsl(222, 12%, 25%)',
        pieOuterStrokeWidth: '1px',

        // Gantt
        gridColor: 'hsl(222, 12%, 30%)',
        doneTaskBkgColor: 'hsl(195, 40%, 25%)',
        doneTaskBorderColor: 'hsl(195, 90%, 55%)',
        activeTaskBkgColor: 'hsl(36, 60%, 25%)',
        activeTaskBorderColor: 'hsl(36, 100%, 55%)',
        taskBkgColor: 'hsl(195, 90%, 20%)',
        taskBorderColor: 'hsl(195, 90%, 55%)',
        taskTextColor: 'hsl(210, 15%, 90%)',
        todayLineColor: 'hsl(36, 100%, 55%)',
        sectionBkgColor: 'hsl(222, 15%, 18%)',
        altSectionBkgColor: 'hsl(222, 15%, 22%)',
        sectionBkgColor2: 'hsl(222, 15%, 20%)',

        // Git graph
        git0: 'hsl(195, 90%, 55%)',
        git1: 'hsl(36, 100%, 55%)',
        git2: 'hsl(150, 60%, 40%)',
        git3: 'hsl(280, 60%, 55%)',
        git4: 'hsl(0, 72%, 55%)',
        git5: 'hsl(195, 60%, 35%)',
        git6: 'hsl(36, 70%, 40%)',
        git7: 'hsl(150, 40%, 30%)',
        gitBranchLabel0: 'hsl(0, 0%, 100%)',
        commitLabelColor: 'hsl(210, 15%, 90%)',
        commitLabelBackground: 'hsl(222, 18%, 15%)',

        // Class/ER diagrams
        classText: 'hsl(210, 15%, 90%)',
        relationColor: 'hsl(195, 60%, 50%)',
        attributeBackgroundColorEven: 'hsl(222, 15%, 18%)',
        attributeBackgroundColorOdd: 'hsl(222, 15%, 22%)',

        // Mindmap / color scale
        cScale0: 'hsl(195, 90%, 20%)',
        cScale1: 'hsl(36, 60%, 18%)',
        cScale2: 'hsl(150, 40%, 18%)',
        cScale3: 'hsl(280, 40%, 20%)',
        cScale4: 'hsl(0, 50%, 20%)',
        cScale5: 'hsl(195, 60%, 15%)',
        cScale6: 'hsl(36, 40%, 15%)',
        cScale7: 'hsl(150, 30%, 15%)',
        cScale8: 'hsl(280, 30%, 16%)',
        cScale9: 'hsl(0, 40%, 16%)',
        cScale10: 'hsl(195, 50%, 18%)',
        cScale11: 'hsl(36, 50%, 16%)',
        cScalePeer0: 'hsl(195, 90%, 55%)',
        cScalePeer1: 'hsl(36, 100%, 55%)',
        cScalePeer2: 'hsl(150, 60%, 40%)',
        cScalePeer3: 'hsl(280, 60%, 55%)',
        cScalePeer4: 'hsl(0, 72%, 55%)',

        // Fonts
        fontFamily: 'inherit',
        fontSize: '14px',
      },
    });
    mermaidInitialized = true;
  }
}

type MermaidBlockProps = {
  children: string;
};

export function MermaidBlock({ children }: MermaidBlockProps) {
  const reactId = useId();
  const mermaidId = `mermaid-${reactId.replace(/:/g, '')}`;
  const [svgHtml, setSvgHtml] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const prevChildrenRef = useRef<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function renderDiagram() {
      // Skip re-render when children haven't changed and we already have SVG
      if (children === prevChildrenRef.current && svgHtml !== null) {
        return;
      }

      ensureMermaidInitialized();
      // Only show loading state on initial render (no existing SVG)
      if (svgHtml === null) {
        setIsLoading(true);
      }
      setError(null);

      try {
        const { svg } = await mermaid.render(mermaidId, children);
        if (!cancelled) {
          setSvgHtml(svg);
          prevChildrenRef.current = children;
        }
      } catch (err: unknown) {
        if (!cancelled) {
          const msg = err instanceof Error ? err.message : String(err);
          setError(msg);
        }
      } finally {
        if (!cancelled) {
          setIsLoading(false);
        }
      }
    }

    renderDiagram();

    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [children]);

  if (error) {
    return (
      <div className="rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
        <p className="font-medium">Mermaid diagram error</p>
        <pre className="mt-1 whitespace-pre-wrap text-xs">{error}</pre>
      </div>
    );
  }

  if (isLoading) {
    return (
      <div className="rounded-md border bg-muted/30 p-4 text-sm text-muted-foreground">
        Rendering diagram...
      </div>
    );
  }

  if (!svgHtml) return null;

  return (
    <div className="mermaid-container overflow-x-auto rounded-md border border-border bg-[hsl(222,18%,15%)] p-4">
      <div className="[&>svg]:max-w-full" dangerouslySetInnerHTML={{ __html: svgHtml }} />
    </div>
  );
}
