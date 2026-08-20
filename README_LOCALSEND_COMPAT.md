# LocalSend compatibility integration for FileShareApp

This branch adds a LocalSend-compatible discovery and HTTP transfer helper set.

What was added

- src/Localsend/Discovery/LocalsendDiscoveryAdapter.cs
  - Listens for LocalSend multicast v2 messages (224.0.0.167:53317)
  - Emits Localsend device info objects with fingerprint/port/protocol fields
  - Can announce a fingerprint (if set) in outgoing announces

- src/Localsend/Http/LocalsendHttpClient.cs
  - Implements LocalSend v2 prepare-upload + upload
  - Supports certificate pinning via SHA-256 fingerprint (pass fingerprint hex to constructor)

- src/Localsend/Http/LocalsendHttpReceiver.cs
  - Minimal reference HTTP receiver for testing (not production-ready)

- src/Localsend/LocalsendCertificate.cs
  - Helper to load a PFX and compute SHA-256 fingerprint (hex)

How to integrate (summary)

1. Generate or obtain a certificate for FileShare and ensure its SAN contains the IP address or hostname clients will use.
   - Example using OpenSSL (replace IP):
     ```bash
     # generate key
     openssl genpkey -algorithm RSA -out fileshare.key -pkeyopt rsa_keygen_bits:2048

     # create config (fileshare.cnf) with subjectAltName = IP.1 = 192.168.1.42

     openssl req -new -key fileshare.key -out fileshare.csr -config fileshare.cnf
     openssl x509 -req -in fileshare.csr -signkey fileshare.key -out fileshare.crt -days 365 -extfile fileshare.cnf -extensions req_ext
     openssl pkcs12 -export -out fileshare.pfx -inkey fileshare.key -in fileshare.crt -passout pass:yourpassword
     ```

2. Load the PFX at FileShare startup, compute its fingerprint and set it on the discovery adapter so announces include the fingerprint.

3. When sending to a discovered LocalSend-capable device, use LocalsendHttpClient and pass the expected fingerprint from discovery to enable pinning.

Notes

- The HTTP receiver included is minimal and intended for testing only. For production, please use ASP.NET Core with streaming multipart parsing.
- The discovery adapter will announce `protocol = "Https"` and `port = 53317` by default; adjust as needed.

Testing

1. Build and run FileShare with the new branch.
2. Start a LocalSend (native or LocalSendCSharp) client on another device.
3. Ensure devices discover each other; verify announce/fingerprint via logs or packet capture.
4. Send a file via the LocalSend flow and verify TLS handshake and transfer.

