# Encrypted LAN transport (0.1.19 and later)

The MOUSE3 protocol requires TLS 1.2 or 1.3 for all application messages.
Version 0.1.18 and earlier use the previous unencrypted mouse protocol.
There is no downgrade path: update both PCs and pair again.

## Pairing and peer verification

A code has the form `MWB3-XXXXXXXX-XXXXXXXX-XXXXXXXX-XXXXXXXX`. Its 32 random
characters come from a 32-symbol alphabet using the platform cryptographic RNG,
giving 160 bits of entropy. The prefix and separators add no entropy. Codes are
reusable secrets: share them privately and keep settings files private.
Legacy 8-character codes and hexadecimal keys are rejected and replaced on load;
obsolete remote codes are cleared while unrelated preferences are preserved.

For each incoming connection, the receiver generates a self-signed P-256
certificate and a fresh 24-byte nonce. Its bounded ASCII greeting contains
`MOUSE3|nonce|certificate-SHA256|HMAC-SHA256`, with the HMAC covering the first three
fields under the pairing secret. The client verifies this HMAC before starting
TLS, then requires an exact certificate hash match during the TLS handshake.
Self-signed certificates are accepted only through this authenticated pin;
arbitrary certificates, changed pins and old protocols are rejected. No CA or
system-wide trust installation is used. On Windows, the private key uses a
temporary user key container for Schannel compatibility, removed when the
certificate is disposed after the handshake. TLS resumption and renegotiation
are disabled.

Inside the verified TLS channel, the client sends the existing nonce-bound
`CLIENT` HMAC proof and waits for the receiver's `SERVER` proof. No mouse,
capability or clipboard data is queued until both proofs complete. Both sides
bound pairing to five seconds, honor cancellation and close failed connections.
The UI stops automatic retries for a rejected identity/code so it can be corrected.

The public greeting is an offline verifier for candidate pairing secrets. This
design relies on a uniformly random 160-bit secret; it is not a PAKE and must
never be changed to accept a user-chosen password or a short numeric code.
TLS ephemeral key agreement protects captured application traffic if the pairing
secret is later disclosed, assuming the negotiated TLS algorithms and endpoints
remain secure. This does not stop someone holding the secret from impersonating
a peer in future connections.

## Application messages and limits

Mouse packets retain their existing framing inside TLS. Each packet's HMAC
covers the protocol, sender direction, session nonce, strictly increasing
sequence number and encoded payload. Clipboard chunks retain their additional
AES-GCM layer with direction-specific authenticated data. Frame, queue, clipboard
size, heartbeat and local input-recovery bounds remain in force.

An attacker able to capture LAN traffic can still see IP addresses, connection
times and traffic sizes/rates. Encryption does not prevent disconnects, packet
dropping, resource exhaustion, or compromise of either PC. A connected peer is
authorized to inject mouse input; enabling clipboard sharing additionally trusts
it with new copied text and files. Keys remain in per-user local settings, subject
to that user's Windows access controls. Do not expose the listening port to the
internet.

See [the verification record](TESTING.md) for the capture/tampering, certificate,
authentication, migration and functional regressions, and remaining hardware checks.
