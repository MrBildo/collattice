import { useMutation, useQueryClient } from '@tanstack/react-query';
import axios from 'axios';
import { addCardLabel, removeCardLabel } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import type { BoardData, CardLabelSummary } from '@/types';

type UseToggleCardLabelOptions = {
  cardId: string;
  boardId: string;
};

type ToggleCardLabelVariables = {
  label: CardLabelSummary;
  // Whether the label is on the card at the moment of the click — true removes it.
  isAssigned: boolean;
};

function patchCardLabels(
  data: BoardData | undefined,
  cardId: string,
  update: (labels: CardLabelSummary[]) => CardLabelSummary[],
): BoardData | undefined {
  if (!data) return data;
  return {
    ...data,
    cards: data.cards.map((c) => (c.id === cardId ? { ...c, labels: update(c.labels) } : c)),
  };
}

function includeLabel(labels: CardLabelSummary[], label: CardLabelSummary): CardLabelSummary[] {
  return labels.some((l) => l.id === label.id) ? labels : [...labels, label];
}

function excludeLabel(labels: CardLabelSummary[], labelId: string): CardLabelSummary[] {
  return labels.filter((l) => l.id !== labelId);
}

function buildToggleMutationKey(cardId: string) {
  return ['cards', cardId, 'labels', 'toggle'] as const;
}

// The server already holds the state this toggle asked for: an add that finds
// the label assigned (409), or a remove that finds it gone (404). An add's 404
// means the card or the label no longer exists, so that one stays an error.
function isAlreadyApplied(error: unknown, isAssigned: boolean): boolean {
  if (!axios.isAxiosError(error)) {
    return false;
  }

  const status = error.response?.status;
  return isAssigned ? status === 404 : status === 409;
}

async function sendToggle(cardId: string, { label, isAssigned }: ToggleCardLabelVariables) {
  try {
    await (isAssigned ? removeCardLabel(cardId, label.id) : addCardLabel(cardId, label.id));
  } catch (error) {
    if (!isAlreadyApplied(error, isAssigned)) {
      throw error;
    }
  }
}

// Toggles one label on a card straight from the board, saving immediately. The
// tile updates optimistically so a run of quick toggles reads instantly; the
// board and the card's own label list are refetched once the last toggle settles.
export function useToggleCardLabel({ cardId, boardId }: UseToggleCardLabelOptions) {
  const queryClient = useQueryClient();
  const boardKey = queryKeys.boards.data(boardId);

  return useMutation({
    mutationKey: buildToggleMutationKey(cardId),
    // One card's toggles reach the server one at a time, in the order they were
    // made. Sent together, a quick double click's two requests race on the
    // server and the loser fails; queued, the second one finds the first done.
    // The tile still updates on every click, because onMutate runs at once.
    // The cost: a request that hangs holds back the toggles behind it until it
    // fails or the HTTP client gives up on it.
    scope: { id: `card-labels-${cardId}` },
    // Board action with an optimistic update: a failure reverts the label on the
    // tile, and the global error floor toasts the reason.
    meta: { errorMessage: "Couldn't update labels" },
    mutationFn: (variables: ToggleCardLabelVariables) => sendToggle(cardId, variables),
    onMutate: async ({ label, isAssigned }: ToggleCardLabelVariables) => {
      await queryClient.cancelQueries({ queryKey: boardKey });
      queryClient.setQueryData<BoardData>(boardKey, (old) =>
        patchCardLabels(old, cardId, (labels) =>
          isAssigned ? excludeLabel(labels, label.id) : includeLabel(labels, label),
        ),
      );
    },
    // Revert only this toggle rather than restoring a whole-cache snapshot, so a
    // failed toggle cannot undo other toggles made while it was in flight.
    onError: (_error, { label, isAssigned }) => {
      queryClient.setQueryData<BoardData>(boardKey, (old) =>
        patchCardLabels(old, cardId, (labels) =>
          isAssigned ? includeLabel(labels, label) : excludeLabel(labels, label.id),
        ),
      );
    },
    onSettled: () => {
      // Refetching while other toggles are still in flight would briefly paint
      // the server's older answer over them; wait for the last one to settle.
      if (queryClient.isMutating({ mutationKey: buildToggleMutationKey(cardId) }) > 1) return;
      queryClient.invalidateQueries({ queryKey: boardKey });
      // The card dialog reads labels from its own query, which the board's
      // change events do not refresh — without this, opening the card right
      // after a toggle would show the old labels.
      queryClient.invalidateQueries({ queryKey: queryKeys.cards.labels(cardId) });
    },
  });
}
