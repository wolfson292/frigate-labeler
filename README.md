# frigate-labeler

Labels [Frigate+](https://plus.frigate.video) images with Claude, then saves the boxes back to
Frigate+ so you only have to review and click **Verify**.

It talks to the same (undocumented) API the Frigate+ website uses. See
[docs/frigate-plus-api.md](docs/frigate-plus-api.md).

## How it works

1. **Detect.** Claude Opus 5.5 gets the full image, the camera's enabled labels, Frigate's own
   detections as hints, and your notes for that camera. It returns every object it finds.
2. **Tighten.** Each object is cropped, enlarged, and sent back for a tight box. If one box covered
   two objects, it gets split.
3. **Review.** Claude checks every box on the full image plus enlarged views of small objects:
   keep, delete, relabel or re-fit, and adds anything missed. It repeats until a round changes
   nothing (up to 3 rounds). Anything it can't settle becomes a **question for you**.
4. **You review** on the local web app (`dotnet run`): answer Claude's questions, describe
   errors in plain text for Claude to fix, or edit boxes yourself. Then **Approve** saves the
   image to Frigate+ and verifies it, like the editor's **Verify & Save**.

The labeling rules are in [`prompts/guidelines.md`](src/FrigateLabeler/prompts/guidelines.md).
To tune them without rebuilding, put a copy named `guidelines.md` in the folder you run from.

### Camera notes

Things that never change for a camera (a parked golf cart, a statue, the neighbor's bins) go in
`~/.config/frigate-labeler/cameras/<camera>.md`. Claude reads them for every image from that camera
and treats them as instructions. Edit them on the review page ("Notes for camera …"), tick
"remember this for camera …" when you answer a question, or edit the file directly.

## Setup

Requires the .NET 10 SDK and a Claude API key. The first run creates
`~/.config/frigate-labeler/config.json` with a placeholder. Put your key there
(`"anthropicApiKey": "sk-ant-..."`). An `ANTHROPIC_API_KEY` environment variable overrides it.

```bash
cd src/FrigateLabeler
dotnet run -- auth      # paste your Frigate+ refresh token (see below)
dotnet run -- doctor    # checks sign-in, API access and image download
```

**Getting the refresh token:** open https://plus.frigate.video while signed in, then go to
Developer Tools → Application → Local Storage → `https://plus.frigate.video`. Copy the value of the
key ending in `.refreshToken`. It's stored in `~/.config/frigate-labeler/auth.json`, readable only by
you. Cognito refresh tokens usually last about 30 days; run `auth` again when the program says so.

## Usage

```bash
cd src/FrigateLabeler
dotnet run            # opens the web app at http://localhost:5178
```

**Label tab:** pick a camera (or All cameras) and a filter (Unverified, New, All), then:
- click thumbnails to select (shift-click selects a range), then **Label selected**;
- **Label all … in this camera**; or, on All cameras, **Label every unverified image**.

You get the image count and an estimated cost before anything is queued. Labeling runs in the
background, one image at a time; the header shows progress, spend and the remaining estimate, and
**Cancel queue** drops whatever is still waiting.

Each camera also shows the Frigate+ supported labels that aren't enabled on it. Enable the ones you
want in Frigate+ camera settings. Existing images then show as unverified for the new labels, and
the labeler fills in just those labels.

**Review tab:** answer Claude's questions, describe errors for Claude to fix, or edit boxes
yourself, then **Approve, save & verify** (or save without verifying).
- Drag to pan, scroll to zoom.
- Drag a box to move it, or a corner to resize it. Arrow keys nudge (⇧ for ×10, ⌥ to resize).
- <kbd>A</kbd> new box · <kbd>D</kbd> difficult · <kbd>Del</kbd> delete · <kbd>Tab</kbd> next box
  · <kbd>H</kbd> hide boxes · <kbd>F</kbd> fit · <kbd>J</kbd>/<kbd>K</kbd> next/previous image.
- Grey ✓ boxes are labels already verified on Frigate+. They're kept exactly as they are.

### Per-label verification

Frigate+ verifies each label separately. When an image is already verified for some labels, the
labeler only works on the labels it still needs. The verified boxes are kept unchanged and sent
back with the new ones, and **Verify** marks just the new labels as verified, like the Frigate+ editor.

Command-line alternatives: `label`, `submit`, `queue`, `cameras` (also lists supported labels that
aren't enabled), `notes --camera NAME`, `doctor`, and `try --file IMG --labels a,b`. Run
`dotnet run -- help` for every option.

## Running it in Docker (behind SWAG)

The `deploy/` folder has everything for a home server:

- `docker-compose.yml`: the app with two named volumes, `/config` (Frigate+ sign-in, camera notes)
  and `/data` (images and results). It joins SWAG's Docker network and publishes no ports.
- `frigate-labeler.subfolder.conf`: SWAG proxy config serving `https://<domain>/frigate-labeler/`,
  with Authelia enabled. **Keep authentication
  on:** the app has no login of its own, and anyone who can reach it can spend your Claude credit
  and change your Frigate+ labels.
- `.env.example`: the environment variables (`ANTHROPIC_API_KEY`, the first-start
  `FRIGATE_PLUS_REFRESH_TOKEN`, `SWAG_NETWORK`).

With Portainer: build the image on the host first (Portainer's agent can't build), e.g.
`docker build -t frigate-labeler:latest https://github.com/wolfson292/frigate-labeler.git#main`,
then **Stacks → Add stack → Repository**, this repo, compose path `deploy/portainer-stack.yml`,
and set the environment variables there. With plain Docker:
`cd deploy && cp .env.example .env` (fill it in), then `docker compose up -d --build`.

When the Frigate+ sign-in expires, the page shows a banner where you paste a new refresh token.

Settings can also come from environment variables: `FRIGATE_LABELER_CONFIG`, `FRIGATE_LABELER_DATA`,
`FRIGATE_LABELER_HOST`, `FRIGATE_LABELER_PORT` and `FRIGATE_LABELER_BASE_PATH`.

## Cost

Each image is one detection call plus one refinement call per object. The program prints
estimated cost per image and per run, at Opus 5.5 list prices.
