// gClaudeTokenMonitor pages — boot 演出とコピーボタンだけの最小 JS。
(function () {
  "use strict";

  // ヒーローの起動シーケンス（reduced-motion では即時表示）
  var boot = document.getElementById("boot");
  if (boot) {
    var lines = JSON.parse(boot.getAttribute("data-lines"));
    var reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (reduce) {
      boot.innerHTML = lines.map(function (l) { return l[1]; }).join("\n") +
        '\n<span class="cursor">▮</span>';
    } else {
      boot.innerHTML = '<span class="cursor">▮</span>';
      var i = 0;
      (function next() {
        if (i >= lines.length) return;
        var delay = lines[i][0];
        var html = lines[i][1];
        i++;
        setTimeout(function () {
          var done = lines.slice(0, i).map(function (l) { return l[1]; }).join("\n");
          boot.innerHTML = done + '\n<span class="cursor">▮</span>';
          next();
        }, delay);
      })();
    }
  }

  // コードブロックのコピー
  document.querySelectorAll(".term").forEach(function (t) {
    var btn = t.querySelector(".copybtn");
    var pre = t.querySelector("pre");
    if (!btn || !pre) return;
    btn.addEventListener("click", function () {
      var text = pre.innerText.replace(/^\$ /gm, "");
      navigator.clipboard.writeText(text).then(function () {
        var old = btn.textContent;
        btn.textContent = "copied!";
        setTimeout(function () { btn.textContent = old; }, 1400);
      });
    });
  });
})();
