// import { EAuthTokenPlatformType, LoginSession } from '../src' // use the following line if you installed the module from npm
import { EAuthTokenPlatformType, LoginSession } from 'steam-session'

// We need to wrap everything in an async function since node <14.8 cannot use await in the top-level context
main()
async function main() {
  // const { EAuthTokenPlatformType, LoginSession } = await import('steam-session')
  // Create our LoginSession and start a QR login session.
  const session = new LoginSession(EAuthTokenPlatformType.MobileApp, { httpProxy: 'http://127.0.0.1:10808' })
  session.loginTimeout = 120000 // timeout after 2 minutes
  const startResult = await session.startWithQR()

  const qrUrl = `https://api.qrserver.com/v1/create-qr-code/?size=200x200&data=${encodeURIComponent(startResult.qrChallengeUrl ?? '')}`
  console.warn(`Open QR code: ${qrUrl}`)

  session.on('remoteInteraction', () => {
    console.warn('Looks like you\'ve scanned the code! Now just approve the login.')
  })

  // No need to handle steamGuardMachineToken since it's only applicable to accounts using email Steam Guard,
  // and such accounts can't be authed using a QR code.

  session.on('authenticated', async () => {
    console.warn('\nAuthenticated successfully! Printing your tokens now...')
    console.warn(`SteamID: ${session.steamID}`)
    console.warn(`Account name: ${session.accountName}`)
    console.warn(`Access token: ${session.accessToken}`)
    console.warn(`Refresh token: ${session.refreshToken}`)

    // We can also get web cookies now that we've negotiated a session
    const webCookies = await session.getWebCookies()
    console.warn('Web session cookies:')
    console.warn(webCookies)
  })

  session.on('timeout', () => {
    console.warn('This login attempt has timed out.')
  })

  session.on('error', (err) => {
    // This should ordinarily not happen. This only happens in case there's some kind of unexpected error while
    // polling, e.g. the network connection goes down or Steam chokes on something.
    console.warn(`ERROR: This login attempt has failed! ${err}`)
  })
}
