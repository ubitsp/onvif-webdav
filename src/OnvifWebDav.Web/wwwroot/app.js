(function () {
  'use strict';

  var state = { cameraId: null, date: null, clips: [], activeId: null };
  var el = {
    camName: document.getElementById('camName'),
    stateDot: document.getElementById('stateDot'),
    stateText: document.getElementById('stateText'),
    dates: document.getElementById('dates'),
    datePick: document.getElementById('datePick'),
    daySummary: document.getElementById('daySummary'),
    player: document.getElementById('player'),
    hint: document.getElementById('playerHint'),
    tlBars: document.getElementById('tlBars'),
    clips: document.getElementById('clips')
  };

  function fmtToday() {
    var d = new Date();
    var m = ('0' + (d.getMonth() + 1)).slice(-2);
    var day = ('0' + d.getDate()).slice(-2);
    return d.getFullYear() + '-' + m + '-' + day;
  }

  function api(url) {
    return fetch(url, { credentials: 'same-origin' }).then(function (r) {
      if (r.status === 401) { location.href = '/login'; throw new Error('未登录'); }
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    });
  }

  // ---------------- 状态条 ----------------
  function pollStatus() {
    api('/api/status').then(function (s) {
      el.stateText.textContent = s.state + ' · 队列 ' + s.queue + ' · 今日 ' + s.todayClips + ' 段';
      el.stateDot.className = 'dot ' + (s.stateCode === 7 ? 'err' : (s.ffmpegAlive ? 'ok' : 'warn'));
      el.stateText.title = (s.codec || '') + ' ' + (s.error || '');
    }).catch(function () { });
  }

  // ---------------- 日期 ----------------
  function loadDates() {
    return api('/api/dates?cameraId=' + encodeURIComponent(state.cameraId || ''))
      .then(function (dates) {
        el.dates.innerHTML = '';
        if (!dates || !dates.length) {
          el.dates.innerHTML = '<div class="muted" style="padding:8px">暂无录像</div>';
          return null;
        }
        dates.forEach(function (d) {
          var item = document.createElement('div');
          item.className = 'date-item';
          item.dataset.date = d;
          item.innerHTML = '<span>' + d + '</span>';
          item.onclick = function () { selectDate(d); };
          el.dates.appendChild(item);
        });
        return dates;
      });
  }

  function selectDate(d) {
    state.date = d;
    el.datePick.value = d;
    Array.prototype.forEach.call(el.dates.children, function (c) {
      c.classList.toggle('active', c.dataset.date === d);
    });
    loadClips();
  }

  // ---------------- 片段 ----------------
  function loadClips() {
    if (!state.date) return;
    el.daySummary.textContent = '加载中…';
    api('/api/clips?cameraId=' + encodeURIComponent(state.cameraId || '') +
        '&date=' + encodeURIComponent(state.date))
      .then(function (clips) {
        state.clips = clips || [];
        renderClips();
        renderTimeline();
        var total = state.clips.reduce(function (a, c) { return a + c.durationSec; }, 0);
        el.daySummary.textContent = state.clips.length
          ? (state.clips.length + ' 段 / 共 ' + Math.round(total / 60) + ' 分钟')
          : '这一天没有录像';
        if (state.clips.length) playClip(state.clips[0], false);
        else { el.player.removeAttribute('src'); el.player.load(); }
      })
      .catch(function (e) {
        el.daySummary.textContent = '读取失败：' + e.message;
      });
  }

  function renderClips() {
    el.clips.innerHTML = '';
    if (!state.clips.length) {
      el.clips.innerHTML = '<li class="empty">这一天没有录像</li>';
      return;
    }

    state.clips.forEach(function (c) {
      var li = document.createElement('li');
      li.className = 'clip';
      li.dataset.id = c.id;

      var img = document.createElement('img');
      img.loading = 'lazy';
      img.src = c.thumbUrl;
      img.alt = c.start;
      img.onerror = function () { img.style.visibility = 'hidden'; };

      var body = document.createElement('div');
      var t = document.createElement('div');
      t.className = 't';
      t.textContent = c.start + ' – ' + c.end;
      var meta = document.createElement('div');
      meta.className = 'meta';
      // 事件类型只看最后两段（tns1:RuleEngine/CellMotionDetector/Motion → CellMotionDetector/Motion）
      var topicTag = '';
      if (c.topics && c.topics.length) {
        var parts = String(c.topics[0]).split('/').filter(function (x) { return x; });
        topicTag = ' · ' + parts.slice(-2).join('/');
      }
      // gapCount > 0 说明合成时中间缺了段，画面会跳
      var gapTag = c.gapCount ? ' · 跳帧' : '';

      meta.textContent = c.durationSec + ' 秒 · ' + c.sizeMb + ' MB' +
        (c.codec ? ' · ' + c.codec.toUpperCase() : '') +
        (c.hasAudio ? ' · 有声' : '') +
        (c.continuedFrom ? ' · 接上一段' : '') + topicTag + gapTag;
      body.appendChild(t);
      body.appendChild(meta);

      var play = document.createElement('button');
      play.textContent = '播放';
      play.onclick = function (e) { e.stopPropagation(); playClip(c, true); };

      li.appendChild(img);
      li.appendChild(body);
      li.appendChild(play);
      li.onclick = function () { playClip(c, true); };
      el.clips.appendChild(li);
    });
  }

  function renderTimeline() {
    el.tlBars.innerHTML = '';
    state.clips.forEach(function (c) {
      var bar = document.createElement('div');
      bar.className = 'tl-bar';
      var left = Math.max(0, Math.min(100, c.startMinute / 1440 * 100));
      var width = Math.max(0.25, (c.endMinute - c.startMinute) / 1440 * 100);
      bar.style.left = left + '%';
      bar.style.width = width + '%';
      bar.title = c.start + ' – ' + c.end + '（' + c.durationSec + 's）';
      bar.onclick = function () { playClip(c, true); };
      bar.dataset.id = c.id;
      el.tlBars.appendChild(bar);
    });
  }

  function playClip(clip, autoplay) {
    state.activeId = clip.id;
    el.hint.className = 'hint';
    el.player.src = clip.mediaUrl;
    if (autoplay) {
      var p = el.player.play();
      if (p && p.catch) p.catch(function () { /* 自动播放被拦，等用户点 */ });
    }
    Array.prototype.forEach.call(document.querySelectorAll('.clip'), function (n) {
      n.classList.toggle('active', n.dataset.id === clip.id);
    });
    Array.prototype.forEach.call(document.querySelectorAll('.tl-bar'), function (n) {
      n.classList.toggle('active', n.dataset.id === clip.id);
    });
  }

  el.player.addEventListener('error', function () {
    if (!el.player.currentSrc) return;
    el.hint.className = 'hint show';
    el.hint.innerHTML = '这段录像浏览器没能解码。<b>多半是摄像头主码流用了 H.265</b>。' +
      '请在电脑上用 Edge / Chrome 打开，或到程序设置里把「H.265 自动转码」打开后重新录制。';
  });

  el.datePick.addEventListener('change', function () {
    if (el.datePick.value) selectDate(el.datePick.value);
  });

  document.getElementById('btnRefresh').onclick = function () {
    loadDates().then(function (dates) {
      if (state.date && dates && dates.indexOf(state.date) >= 0) loadClips();
    });
  };

  // ---------------- 启动 ----------------
  api('/api/cameras').then(function (cams) {
    if (!cams || !cams.length) {
      el.camName.textContent = '没有启用的摄像头';
      return;
    }
    state.cameraId = cams[0].id;
    el.camName.textContent = cams[0].name || cams[0].id;

    loadDates().then(function (dates) {
      var pick = (dates && dates.length) ? dates[0] : fmtToday();
      selectDate(pick);
    });
  }).catch(function (e) {
    el.camName.textContent = '加载失败：' + e.message;
  });

  el.datePick.value = fmtToday();
  pollStatus();
  setInterval(pollStatus, 5000);
})();
