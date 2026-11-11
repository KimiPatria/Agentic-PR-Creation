import { useQuery } from '@tanstack/react-query'
import { NavLink, Navigate, Outlet, Route, Routes } from 'react-router-dom'
import { api } from './api'
import { AuthGate, useSession } from './auth'
import { AdminPage } from './pages/AdminPage'
import { RequestPage } from './pages/RequestPage'
import { RequestsPage } from './pages/RequestsPage'
import { Button } from './ui'

export default function App() {
  return (
    <AuthGate>
      <Routes>
        <Route element={<Shell />}>
          <Route index element={<RequestsPage key="mine" scope="mine" />} />
          <Route path="requests/:id" element={<RequestPage />} />
          <Route path="review" element={<ReviewerOnly />} />
          <Route path="admin" element={<AdminOnly />} />
          <Route path="admin/requests" element={<AdminOnly all />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </AuthGate>
  )
}

function ReviewerOnly() {
  return useSession().isReviewer ? <RequestsPage key="review" scope="review" /> : <Navigate to="/" replace />
}

function AdminOnly({ all }: { all?: boolean }) {
  const { isAdmin } = useSession()
  if (!isAdmin) return <Navigate to="/" replace />
  return all ? <RequestsPage key="all" scope="all" /> : <AdminPage />
}

function Shell() {
  const { me, isReviewer, isAdmin, signOut, devUsers } = useSession()
  const meta = useQuery({ queryKey: ['meta'], queryFn: api.meta, staleTime: Infinity })
  const waiting = useQuery({
    queryKey: ['requests', 'review', 'Submitted'],
    queryFn: () => api.requests('review', 'Submitted'),
    enabled: isReviewer,
    refetchInterval: 15_000,
  })

  const link = ({ isActive }: { isActive: boolean }) =>
    `rounded-md px-3 py-1.5 text-sm font-medium ${isActive ? 'bg-teal-50 text-teal-900' : 'text-slate-600 hover:bg-slate-100'}`

  return (
    <div className="min-h-screen">
      <header className="sticky top-0 z-30 border-b border-slate-200 bg-white">
        <div className="mx-auto flex max-w-screen-2xl flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3">
          <span className="text-base font-semibold text-teal-800">PR Agent</span>
          <nav className="flex flex-wrap gap-1" aria-label="Main">
            <NavLink to="/" end className={link}>
              My requests
            </NavLink>
            {isReviewer && (
              <NavLink to="/review" className={link}>
                Review queue
                {!!waiting.data?.length && <span className="ml-1.5 rounded-full bg-teal-700 px-1.5 py-0.5 text-xs text-white">{waiting.data.length}</span>}
              </NavLink>
            )}
            {isAdmin && (
              <>
                <NavLink to="/admin/requests" className={link}>
                  All requests
                </NavLink>
                <NavLink to="/admin" end className={link}>
                  Administration
                </NavLink>
              </>
            )}
          </nav>
          <div className="ml-auto flex items-center gap-3 text-sm">
            <span className="text-right leading-tight">
              <span className="block font-medium">{me.name}</span>
              <span className="block text-xs text-slate-500">{me.roles.filter((r) => r !== 'Requestor').join(', ') || 'Requestor'}</span>
            </span>
            <Button small onClick={signOut}>
              {devUsers ? 'Switch user' : 'Sign out'}
            </Button>
          </div>
        </div>
        {meta.data?.sapSimulated && (
          <div className="border-t border-amber-200 bg-amber-50 px-4 py-1.5 text-center text-xs text-amber-900" role="status">
            <strong>SAP is simulated.</strong> Master data and budgets are sample data, and PR numbers are not real. Nothing is written to SAP.
          </div>
        )}
      </header>
      <main className="mx-auto max-w-screen-2xl px-4 py-6">
        <Outlet />
      </main>
    </div>
  )
}
