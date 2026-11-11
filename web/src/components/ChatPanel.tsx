import { useEffect, useRef, useState } from 'react'
import type { DraftInput, Proposal } from '../api'
import { api, chat } from '../api'
import { Badge, Button, money } from '../ui'

interface Turn {
  role: 'user' | 'assistant'
  text: string
  activity: string | null
  proposals: Proposal[]
  error: string | null
}

const ACTIVITY: Record<string, string> = {
  search_master_data: 'Looking things up in SAP',
  get_budget: 'Checking the budget in SAP',
  propose_form_fill: 'Checking the suggestion against the PR rules',
}

const EXAMPLES = [
  'I need 5 laptops for new engineers, plus a vendor to set them up.',
  'Two 48-port network switches for the Data Centre Refresh project.',
  'Saya butuh pelatihan Excel untuk 15 staf Finance.',
]

/**
 * The assistant. The requestor describes the need; the assistant answers and may propose a form
 * fill. A proposal changes nothing until the requestor presses "Apply to form".
 */
export function ChatPanel({ draft, requestId, onApply }: { draft: DraftInput; requestId: string | null; onApply: (proposal: Proposal) => void }) {
  const [sessionId] = useState(() => crypto.randomUUID())
  const [turns, setTurns] = useState<Turn[]>([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [decided, setDecided] = useState<Record<string, 'applied' | 'dismissed'>>({})
  const bottom = useRef<HTMLDivElement>(null)
  const abort = useRef<AbortController | null>(null)

  // Braces matter: newer browsers return a promise from scrollIntoView, and an effect must not return one.
  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'nearest' })
  }, [turns])
  useEffect(() => () => abort.current?.abort(), [])

  const patchLast = (change: (turn: Turn) => Turn) => setTurns((all) => [...all.slice(0, -1), change(all[all.length - 1])])

  const send = async (message: string) => {
    const text = message.trim()
    if (!text || busy) return
    setInput('')
    setBusy(true)
    setTurns((all) => [
      ...all,
      { role: 'user', text, activity: null, proposals: [], error: null },
      { role: 'assistant', text: '', activity: 'Thinking', proposals: [], error: null },
    ])

    abort.current = new AbortController()
    try {
      await chat(
        { sessionId, message: text, form: draft, requestId },
        (event) => {
          if (event.type === 'text') patchLast((t) => ({ ...t, text: t.text + event.delta, activity: null }))
          else if (event.type === 'tool') patchLast((t) => ({ ...t, activity: ACTIVITY[event.name] ?? 'Working' }))
          else if (event.type === 'proposal') patchLast((t) => ({ ...t, proposals: [...t.proposals, event.proposal] }))
          else if (event.type === 'error') patchLast((t) => ({ ...t, error: event.message, activity: null }))
          else patchLast((t) => ({ ...t, activity: null }))
        },
        abort.current.signal,
      )
      patchLast((t) => ({ ...t, activity: null }))
    } catch (error) {
      if (!(error instanceof DOMException && error.name === 'AbortError'))
        patchLast((t) => ({ ...t, activity: null, error: error instanceof Error ? error.message : 'The assistant could not be reached.' }))
    } finally {
      setBusy(false)
    }
  }

  const decide = (proposal: Proposal, outcome: 'applied' | 'dismissed') => {
    setDecided((all) => ({ ...all, [proposal.id]: outcome }))
    if (outcome === 'applied') onApply(proposal)
    // Kept for audit and for measuring how often suggestions are accepted. Failure here is not the user's problem.
    api.saveSuggestionOutcome({ sessionId, requestId, proposal, outcome: { outcome } }).catch(() => undefined)
  }

  return (
    <section className="flex h-full min-h-96 flex-col rounded-lg border border-slate-200 bg-white" aria-label="PR assistant">
      <header className="border-b border-slate-100 px-4 py-3">
        <h2 className="text-sm font-semibold">PR assistant</h2>
        <p className="text-xs text-slate-500">Describe what you need. It suggests; you decide. It cannot submit or create anything.</p>
      </header>

      <div className="flex-1 space-y-3 overflow-y-auto px-4 py-3" aria-live="polite">
        {turns.length === 0 && (
          <div className="space-y-2">
            <p className="text-xs text-slate-500">Try:</p>
            {EXAMPLES.map((example) => (
              <button
                key={example}
                type="button"
                onClick={() => void send(example)}
                className="block w-full rounded-md border border-slate-200 px-3 py-2 text-left text-xs text-slate-700 hover:border-teal-600 hover:bg-teal-50"
              >
                {example}
              </button>
            ))}
          </div>
        )}

        {turns.map((turn, index) =>
          turn.role === 'user' ? (
            <p key={index} className="ml-8 rounded-lg bg-teal-700 px-3 py-2 text-sm whitespace-pre-wrap text-white">
              {turn.text}
            </p>
          ) : (
            <div key={index} className="mr-4 space-y-2">
              {turn.proposals.map((proposal) => (
                <ProposalCard key={proposal.id} proposal={proposal} state={decided[proposal.id]} onDecide={(outcome) => decide(proposal, outcome)} />
              ))}
              {turn.text && <p className="rounded-lg bg-slate-100 px-3 py-2 text-sm whitespace-pre-wrap text-slate-800">{turn.text}</p>}
              {turn.activity && <p className="animate-pulse text-xs text-slate-500">{turn.activity}…</p>}
              {turn.error && <p className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-800">{turn.error}</p>}
            </div>
          ),
        )}
        <div ref={bottom} />
      </div>

      <form
        className="flex gap-2 border-t border-slate-100 p-3"
        onSubmit={(event) => {
          event.preventDefault()
          void send(input)
        }}
      >
        <textarea
          className="min-h-10 flex-1 resize-none rounded-md border border-slate-300 px-2.5 py-2 text-sm"
          rows={2}
          value={input}
          placeholder="What do you need to buy?"
          aria-label="Message to the PR assistant"
          onChange={(event) => setInput(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && !event.shiftKey) {
              event.preventDefault()
              void send(input)
            }
          }}
        />
        <Button variant="primary" type="submit" disabled={busy || !input.trim()}>
          Send
        </Button>
      </form>
    </section>
  )
}

function ProposalCard({ proposal, state, onDecide }: { proposal: Proposal; state: 'applied' | 'dismissed' | undefined; onDecide: (outcome: 'applied' | 'dismissed') => void }) {
  return (
    <div className="rounded-lg border border-teal-200 bg-teal-50/60 p-3">
      <div className="mb-2 flex items-center justify-between gap-2">
        <span className="text-xs font-semibold text-teal-900">Suggested form fill</span>
        {state === 'applied' && <Badge tone="good">Applied</Badge>}
        {state === 'dismissed' && <Badge tone="neutral">Dismissed</Badge>}
      </div>
      <p className="text-sm font-medium text-slate-900">{proposal.title}</p>
      <ul className="mt-2 space-y-2">
        {proposal.lines.map((line) => (
          <li key={line.lineNo} className="rounded-md bg-white px-2.5 py-2 text-xs text-slate-700 ring-1 ring-slate-200">
            <span className="font-medium text-slate-900">
              {line.lineNo}. {line.shortText}
            </span>
            <span className="mt-1 flex flex-wrap gap-1">
              {line.itemType && <Badge>{line.itemType}</Badge>}
              {line.budgetType && <Badge>{line.budgetType.toUpperCase()}</Badge>}
              {line.materialGroup && <Badge>Group {line.materialGroup}</Badge>}
              {line.quantity != null && <Badge>{`${line.quantity} ${line.uom ?? ''}`.trim()}</Badge>}
              {line.unitPrice != null && <Badge>{money(line.unitPrice, proposal.currency)}</Badge>}
              {line.costCenter && <Badge>{line.costCenter}</Badge>}
              {line.wbs && <Badge>{line.wbs}</Badge>}
            </span>
            {proposal.reasons[String(line.lineNo)] && <span className="mt-1 block text-slate-500">{proposal.reasons[String(line.lineNo)]}</span>}
          </li>
        ))}
      </ul>
      {!state && (
        <div className="mt-3 flex gap-2">
          <Button variant="primary" small onClick={() => onDecide('applied')}>
            Apply to form
          </Button>
          <Button small onClick={() => onDecide('dismissed')}>
            Dismiss
          </Button>
        </div>
      )}
      <p className="mt-2 text-xs text-slate-500">Applying replaces the lines on the form. Nothing is saved or submitted until you do it.</p>
    </div>
  )
}
