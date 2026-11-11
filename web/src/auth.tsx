import { useQuery, useQueryClient } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { createContext, useContext, useEffect, useMemo, useState } from 'react'
import { AuthProvider as OidcProvider, useAuth as useOidc } from 'react-oidc-context'
import type { Me, PublicConfig } from './api'
import { api, setAuthHeaders } from './api'
import { Button, Card, ErrorNote, Loading, Note } from './ui'

interface Session {
  me: Me
  isReviewer: boolean
  isAdmin: boolean
  signOut: () => void
  /** Development sign-in only: the personas that can be switched to. */
  devUsers: PublicConfig['devUsers']
}

const SessionContext = createContext<Session | null>(null)

export function useSession(): Session {
  const session = useContext(SessionContext)
  if (!session) throw new Error('useSession must be used inside <AuthGate>.')
  return session
}

const DEV_USER_KEY = 'pragent.devUser'

/** Signs the user in (Cognito, or a demo persona in development) before rendering the app. */
export function AuthGate({ children }: { children: ReactNode }) {
  const config = useQuery({ queryKey: ['public-config'], queryFn: api.publicConfig, staleTime: Infinity, retry: 1 })

  if (config.isPending) return <Centered><Loading label="Connecting" /></Centered>
  if (config.isError) return <Centered><ErrorNote error={config.error} /></Centered>

  return config.data.authMode === 'Dev' ? (
    <DevGate config={config.data}>{children}</DevGate>
  ) : (
    <CognitoGate>{children}</CognitoGate>
  )
}

// ---- Development: pick a persona ---------------------------------------------------------------

function DevGate({ config, children }: { config: PublicConfig; children: ReactNode }) {
  const queryClient = useQueryClient()
  const [email, setEmail] = useState<string | null>(() => localStorage.getItem(DEV_USER_KEY))

  // Set synchronously during render so the very first request already carries the persona.
  setAuthHeaders((): Record<string, string> => (email ? { 'X-Dev-User': email } : {}))

  const choose = (next: string | null) => {
    if (next) localStorage.setItem(DEV_USER_KEY, next)
    else localStorage.removeItem(DEV_USER_KEY)
    queryClient.clear()
    setEmail(next)
  }

  if (!email) {
    return (
      <Centered>
        <Card title="Development sign-in">
          <div className="space-y-3">
            <Note tone="warn">This sign-in exists for local development only. Real users sign in through Cognito.</Note>
            <p className="text-sm text-slate-600">Choose who to be:</p>
            <ul className="space-y-2">
              {config.devUsers?.map((user) => (
                <li key={user.email}>
                  <button
                    type="button"
                    onClick={() => choose(user.email)}
                    className="w-full rounded-md border border-slate-200 px-3 py-2 text-left hover:border-teal-600 hover:bg-teal-50"
                  >
                    <span className="block text-sm font-medium">{user.name}</span>
                    <span className="block text-xs text-slate-500">{user.email}</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        </Card>
      </Centered>
    )
  }

  return (
    <SessionLoader key={email} signOut={() => choose(null)} devUsers={config.devUsers}>
      {children}
    </SessionLoader>
  )
}

// ---- Cognito ---------------------------------------------------------------------------------

const cognito = {
  authority: import.meta.env.VITE_COGNITO_AUTHORITY as string | undefined,
  clientId: import.meta.env.VITE_COGNITO_CLIENT_ID as string | undefined,
  domain: import.meta.env.VITE_COGNITO_DOMAIN as string | undefined,
}

function CognitoGate({ children }: { children: ReactNode }) {
  if (!cognito.authority || !cognito.clientId) {
    return (
      <Centered>
        <Note tone="bad">
          The API expects Cognito sign-in, but the web app is not configured for it. Set VITE_COGNITO_AUTHORITY, VITE_COGNITO_CLIENT_ID and
          VITE_COGNITO_DOMAIN in web/.env.local (see web/.env.example).
        </Note>
      </Centered>
    )
  }

  return (
    <OidcProvider
      authority={cognito.authority}
      client_id={cognito.clientId}
      redirect_uri={window.location.origin + '/'}
      response_type="code"
      scope="openid email profile"
      onSigninCallback={() => window.history.replaceState({}, document.title, window.location.pathname)}
    >
      <CognitoSession>{children}</CognitoSession>
    </OidcProvider>
  )
}

function CognitoSession({ children }: { children: ReactNode }) {
  const oidc = useOidc()
  const idToken = oidc.user?.id_token

  // The API validates the ID token: it carries the email, name and groups.
  setAuthHeaders((): Record<string, string> => (idToken ? { Authorization: `Bearer ${idToken}` } : {}))

  if (oidc.isLoading) return <Centered><Loading label="Signing in" /></Centered>
  if (oidc.error) return <Centered><ErrorNote error={oidc.error} /></Centered>

  if (!oidc.isAuthenticated || !idToken) {
    return (
      <Centered>
        <Card title="PR Agent">
          <div className="space-y-3">
            <p className="text-sm text-slate-600">Sign in with your company account to raise or review purchase requests.</p>
            <Button variant="primary" onClick={() => void oidc.signinRedirect()}>
              Sign in
            </Button>
          </div>
        </Card>
      </Centered>
    )
  }

  const signOut = () => {
    void oidc.removeUser()
    if (cognito.domain) {
      const back = encodeURIComponent(window.location.origin + '/')
      window.location.href = `${cognito.domain.replace(/\/$/, '')}/logout?client_id=${cognito.clientId}&logout_uri=${back}`
    }
  }

  return (
    <SessionLoader key={oidc.user?.profile.sub} signOut={signOut} devUsers={null}>
      {children}
    </SessionLoader>
  )
}

// ---- Shared ----------------------------------------------------------------------------------

function SessionLoader({ signOut, devUsers, children }: { signOut: () => void; devUsers: PublicConfig['devUsers']; children: ReactNode }) {
  const me = useQuery({ queryKey: ['me'], queryFn: api.me, staleTime: 5 * 60_000 })

  const session = useMemo<Session | null>(
    () =>
      me.data
        ? { me: me.data, isReviewer: me.data.roles.includes('Reviewer'), isAdmin: me.data.roles.includes('Admin'), signOut, devUsers }
        : null,
    [me.data, signOut, devUsers],
  )

  useEffect(() => {
    document.title = 'PR Agent'
  }, [])

  if (me.isPending) return <Centered><Loading label="Loading your profile" /></Centered>
  if (me.isError || !session) {
    return (
      <Centered>
        <div className="space-y-3">
          <ErrorNote error={me.error} />
          <Button onClick={signOut}>Sign out</Button>
        </div>
      </Centered>
    )
  }

  return <SessionContext.Provider value={session}>{children}</SessionContext.Provider>
}

function Centered({ children }: { children: ReactNode }) {
  return (
    <div className="flex min-h-screen items-center justify-center p-6">
      <div className="w-full max-w-md">{children}</div>
    </div>
  )
}
