# Frigate+ web API notes

Observed from the plus.frigate.video web app (October 2026). **Undocumented**, so it may change
without notice.

## Auth

- The web app signs in through the Cognito hosted UI at `auth.frigate.video` (OAuth 2 authorization
  code + PKCE, public client `6q5d80738bn3jot0osfo7k0u6t`, region us-east-2).
- Refresh: `POST https://auth.frigate.video/oauth2/token` (form-encoded)
  `grant_type=refresh_token&client_id=…&refresh_token=…` → `{access_token, id_token, expires_in, token_type}`.
- API calls send `Authorization: Bearer <access_token>`. The **id token gets a 403**.
- The browser keeps the tokens in localStorage under
  `CognitoIdentityServiceProvider.<clientId>.<userId>.{accessToken,idToken,refreshToken}`.

## Endpoints (base `https://api.frigate.video/v1`)

| Method | Path | Notes |
|---|---|---|
| GET | `user/profile` | `{id, email, …}`. `id` is also the image-bucket folder |
| GET | `camera/list` | `{list:[{name, numImages, labels[], verifiedLabelCounts, annotationCounts, …}]}` |
| GET | `camera/{name}/labels` | `{labels:[…]}`: labels enabled for that camera |
| GET | `model/list` | Trained models |
| GET | `image/list` | Query: `camera`, `limit`, `lastImage` (cursor), plus a filter, below. Returns `{list:[{id,camera,verifiedLabels}], lastImageId}` |
| GET | `image/{id}` | `{id, camera, verifiedLabels}` |
| GET | `image/{id}/data` | `{annotations[], suggestions[], falsePositives[]}` |
| PUT | `image/{id}/data` | Save: `{annotations:[…], reviewedSuggestions:[]}` |
| DELETE | `image/{id}/false_positive` | Sent by the editor before the PUT when it saves |

`image/list` filters, matching the web app's tabs:
- New images: `verified=none`
- Unverified: `unverified=<comma-separated camera labels>`
- All images: no filter

Also referenced in the app bundle but not yet exercised: `image/create`, `image/signed_urls`,
`image/patches`, `model/request`, `user/api_key`, `user/subscription`, `user/accept_terms`.

| PUT | `image/{id}/verify` | `{labels:[…]}`: marks labels verified. Verify & Save sends the camera's labels not yet verified on the image, after the data PUT |

`image/list` returns roughly, not exactly, `limit` items (filters apply after paging); `limit` > 100
is rejected. Page until an empty page.

Verification is per label. Adding a label to a camera makes its existing images unverified for that
label only. The site's label catalog is `LABEL_GROUPS` in its JS bundle (44 supported labels);
`CANDIDATE_LABEL_GROUPS` lists labels that aren't supported yet.

## Annotation format

Coordinates are normalized to 0–1, and `x,y` is the top-left corner:

```json
{"label": "waste_bin", "x": 0.48125, "y": 0.4764, "w": 0.0481, "h": 0.0764, "difficult": false}
```

`falsePositives[]` are the detections Frigate uploaded with the image:
`{label, score, x, y, w, h, regionX, regionY, regionW, regionH, model_type, model_hash, detector_type}`.

## Images

`https://images.frigate.video/<userId>/<imageId>.jpg` (and `-thumb.jpg`) is served by CloudFront and
authorized with **signed cookies**. `GET user/profile` responds with `Set-Cookie` for
`CloudFront-Policy`, `CloudFront-Signature`, `CloudFront-Key-Pair-Id` and `CloudFront-Expiration`
(`Domain=.frigate.video; Path=/`). Send them with the image request; without them it gets a 403.
