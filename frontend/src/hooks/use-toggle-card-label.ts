import { useMutation, useQueryClient } from '@tanstack/react-query';
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

function withLabel(labels: CardLabelSummary[], label: CardLabelSummary): CardLabelSummary[] {
  return labels.some((l) => l.id === label.id) ? labels : [...labels, label];
}

function withoutLabel(labels: CardLabelSummary[], labelId: string): CardLabelSummary[] {
  return labels.filter((l) => l.id !== labelId);
}

function cardLabelsMutationKey(cardId: string) {
  return ['cards', cardId, 'labels', 'toggle'] as const;
}

// Toggles one label on a card straight from the board, saving immediately. The
// tile updates optimistically so a run of quick toggles reads instantly; the
// board and the card's own label list are refetched once the last toggle settles.
export function useToggleCardLabel({ cardId, boardId }: UseToggleCardLabelOptions) {
  const queryClient = useQueryClient();
  const boardKey = queryKeys.boards.data(boardId);

  return useMutation({
    mutationKey: cardLabelsMutationKey(cardId),
    // Board action with an optimistic update: a failure reverts the label on the
    // tile, and the global error floor toasts the reason.
    meta: { errorMessage: "Couldn't update labels" },
    mutationFn: ({ label, isAssigned }: ToggleCardLabelVariables) =>
      isAssigned ? removeCardLabel(cardId, label.id) : addCardLabel(cardId, label.id),
    onMutate: async ({ label, isAssigned }: ToggleCardLabelVariables) => {
      await queryClient.cancelQueries({ queryKey: boardKey });
      queryClient.setQueryData<BoardData>(boardKey, (old) =>
        patchCardLabels(old, cardId, (labels) =>
          isAssigned ? withoutLabel(labels, label.id) : withLabel(labels, label),
        ),
      );
    },
    // Revert only this toggle rather than restoring a whole-cache snapshot, so a
    // failed toggle cannot undo other toggles made while it was in flight.
    onError: (_error, { label, isAssigned }) => {
      queryClient.setQueryData<BoardData>(boardKey, (old) =>
        patchCardLabels(old, cardId, (labels) =>
          isAssigned ? withLabel(labels, label) : withoutLabel(labels, label.id),
        ),
      );
    },
    onSettled: () => {
      // Refetching while other toggles are still in flight would briefly paint
      // the server's older answer over them; wait for the last one to settle.
      if (queryClient.isMutating({ mutationKey: cardLabelsMutationKey(cardId) }) > 1) return;
      queryClient.invalidateQueries({ queryKey: boardKey });
      // The card dialog reads labels from its own query, which the board's
      // change events do not refresh — without this, opening the card right
      // after a toggle would show the old labels.
      queryClient.invalidateQueries({ queryKey: queryKeys.cards.labels(cardId) });
    },
  });
}
