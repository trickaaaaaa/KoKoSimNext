// ブラウザ版のセーブを IndexedDB へ書き出す（Web 体験版 #1323）。
// Unity の WebGL はファイルをいったんメモリ（/idbfs）に書くだけで、FS.syncfs(false) を呼んだときだけ
// IndexedDB へ移る。呼ばないとページを開き直したときにセーブが消える（2026-11-23 の Web お試しビルド）。
mergeInto(LibraryManager.library, {
  KokoSim_FlushStorage: function () {
    FS.syncfs(false, function (err) {
      if (err) console.error("[KokoSim] セーブの書き出しに失敗: " + err);
    });
  },
});
