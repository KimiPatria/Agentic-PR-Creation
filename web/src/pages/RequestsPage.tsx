import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import type { RequestStatus } from '../api'
import { api } from '../api'
import { Button, Card, Empty, ErrorNote, Loading, SimulatedTag, StatusBadge, Table, cell, dateTime, inputClass, money } from '../ui'

const TITLES = {
  mine: { title: 'My requests', empty: 'You have not raised a request yet.' },
  review: { title: 'Review queue', empty: 'Nothing is waiting for you.' },
  all: { title: 'All requests', empty: 'No requests yet.' },
}

const STATUSES: RequestStatus[] = ['Draft', 'Submitted', 'SentBack', 'Approved', 'Creating', 'Created', 'CreationFailed']

export function RequestsPage({ scope }: { scope: 'mine' | 'review' | 'all' }) {
  const navigate = useNavigate()
  // A reviewer opens the queue to see what needs a decision, so that is the default filter there.
  const [status, setStatus] = useState<RequestStatus | ''>(scope === 'review' ? 'Submitted' : '')
  const list = useQuery({
    queryKey: ['requests', scope, status],
    queryFn: () => api.requests(scope, status || undefined),
    refetchInterval: 15_000,
  })

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h1 className="text-xl font-semibold">{TITLES[scope].title}</h1>
        <div className="flex items-center gap-2">
          <select className={`${inputClass} w-auto`} value={status} aria-label="Filter by status" onChange={(event) => setStatus(event.target.value as RequestStatus | '')}>
            <option value="">Any status</option>
            {STATUSES.filter((s) => scope !== 'review' || s !== 'Draft').map((s) => (
              <option key={s} value={s}>
                {s === 'SentBack' ? 'Sent back' : s === 'CreationFailed' ? 'Creation failed' : s}
              </option>
            ))}
          </select>
          {scope === 'mine' && (
            <Button variant="primary" onClick={() => navigate('/requests/new')}>
              New request
            </Button>
          )}
        </div>
      </div>

      <Card>
        {list.isPending ? (
          <Loading />
        ) : list.isError ? (
          <ErrorNote error={list.error} />
        ) : list.data.length === 0 ? (
          <Empty>{status ? 'No requests with this status.' : TITLES[scope].empty}</Empty>
        ) : (
          <Table head={['Reference', 'Title', scope === 'mine' ? 'Section' : 'Requestor', 'Status', 'Total', 'SAP PR', 'Updated']}>
            {list.data.map((request) => (
              <tr key={request.id} className="hover:bg-slate-50">
                <td className={cell}>
                  <Link to={`/requests/${request.id}`} className="font-medium text-teal-800 hover:underline">
                    {request.reference}
                  </Link>
                </td>
                <td className={cell}>
                  {request.title || <span className="text-slate-400">Untitled</span>}
                  <span className="block text-xs text-slate-500">
                    {request.lineCount} line{request.lineCount === 1 ? '' : 's'}
                  </span>
                </td>
                <td className={cell}>{scope === 'mine' ? (request.sectionName ?? '-') : request.requestorName}</td>
                <td className={cell}>
                  <StatusBadge status={request.status} />
                </td>
                <td className={cell}>{money(request.total, request.currency)}</td>
                <td className={cell}>
                  {request.prNumbers.length === 0 ? (
                    '-'
                  ) : (
                    <span className="flex flex-wrap items-center gap-1">
                      {request.prNumbers.join(', ')} {request.isSimulated && <SimulatedTag />}
                    </span>
                  )}
                </td>
                <td className={cell}>{dateTime(request.updatedAt)}</td>
              </tr>
            ))}
          </Table>
        )}
      </Card>
    </div>
  )
}
