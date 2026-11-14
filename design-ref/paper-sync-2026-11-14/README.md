# Paper sync 2026-11-14 (temporary, read by the Mac session, then removed)

Built with tools/paper-sync from the game on branch claude/project-thread-vhf05q (the ui-inspo pull request).
Every window was checked against the game's own pixels: under 1% of pixels differ by more than 60/255 in each of the 17 states.

- manifest.json: which HTML goes into which artboard of page "UIs---V2--human", the artboard size after the write, the one rename,
  and the colour changes for the "system-info" tile.
- FRAME.html: the artboard's new children (a "Windows" row of captioned windows, then the "User stories" panel).
- FRAME/: the same in pieces (00_row.html is the empty row; NN_column.html goes inside it in order; 99_stories.html after the row).
- img/: every picture the HTML uses, referenced as paper-asset:///Users/paulgazagnaire/Repos/paper-exports/2026-11-14-sync/img/NAME.png.
