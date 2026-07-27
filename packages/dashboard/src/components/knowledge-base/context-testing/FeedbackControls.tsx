/**
 * Feedback Controls
 *
 * Standalone feedback control component for rating context chunk relevance.
 * Provides rating buttons and an optional comment field.
 */

import { useState, type JSX } from 'react';

export type FeedbackRating = 'relevant' | 'irrelevant' | 'partially_relevant';

export interface FeedbackControlsProps {
  chunkId: string;
  onSubmit: (chunkId: string, rating: FeedbackRating, comment?: string) => void;
  submitted?: boolean;
}

export function FeedbackControls({ chunkId, onSubmit, submitted }: FeedbackControlsProps): JSX.Element {
  const [selectedRating, setSelectedRating] = useState<FeedbackRating | null>(null);
  const [comment, setComment] = useState('');
  const [showComment, setShowComment] = useState(false);

  const handleSubmit = (rating: FeedbackRating) => {
    setSelectedRating(rating);
    onSubmit(chunkId, rating, comment || undefined);
  };

  if (submitted || selectedRating) {
    return (
      <div data-testid="feedback-submitted" style={{ fontSize: '12px', color: '#22c55e', fontWeight: 500 }}>
        Feedback submitted: {selectedRating ?? 'done'}
      </div>
    );
  }

  const buttonBase = {
    padding: '4px 10px',
    borderRadius: '4px',
    backgroundColor: 'transparent',
    cursor: 'pointer',
    fontSize: '12px',
  };

  return (
    <div data-testid="feedback-controls-standalone">
      <div style={{ display: 'flex', gap: '6px', alignItems: 'center' }}>
        <button
          data-testid="feedback-relevant"
          onClick={() => handleSubmit('relevant')}
          style={{ ...buttonBase, border: '1px solid #22c55e', color: '#22c55e' }}
        >
          Relevant
        </button>
        <button
          data-testid="feedback-partial"
          onClick={() => handleSubmit('partially_relevant')}
          style={{ ...buttonBase, border: '1px solid #f59e0b', color: '#f59e0b' }}
        >
          Partial
        </button>
        <button
          data-testid="feedback-irrelevant"
          onClick={() => handleSubmit('irrelevant')}
          style={{ ...buttonBase, border: '1px solid #ef4444', color: '#ef4444' }}
        >
          Irrelevant
        </button>
        <button
          data-testid="toggle-comment"
          onClick={() => setShowComment(!showComment)}
          style={{ ...buttonBase, border: '1px solid #d1d5db', color: '#6b7280' }}
        >
          Comment
        </button>
      </div>

      {showComment && (
        <div style={{ marginTop: '6px' }}>
          <input
            data-testid="feedback-comment"
            value={comment}
            onChange={(e) => setComment(e.target.value)}
            placeholder="Optional comment..."
            style={{
              width: '100%',
              padding: '4px 8px',
              border: '1px solid #d1d5db',
              borderRadius: '4px',
              fontSize: '12px',
            }}
          />
        </div>
      )}
    </div>
  );
}
