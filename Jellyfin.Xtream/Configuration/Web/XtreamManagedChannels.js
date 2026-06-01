export default function (view) {
  // Category automatically assigned to every channel that supports catch-up (kept in sync with XtreamController).
  const CATCHUP_CATEGORY = "A l'heure calédo";

  const formatProgram = (entry) => {
    const start = new Date(entry.Start);
    const end = new Date(entry.End);
    const time = (d) => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    return `${time(start)} - ${time(end)} · ${entry.Title}`;
  };

  const createChannelRow = (Xtream, channel, overrides, allChannels) => {
    const tr = document.createElement('tr');
    tr.dataset['channelId'] = channel.Id;

    // Seed the category with the auto-suggested value so unedited channels still get categorized on save.
    if (!overrides.Category && channel.SuggestedCategory) {
      overrides.Category = channel.SuggestedCategory;
    }

    // --- Logo ---
    let td = document.createElement('td');
    if (channel.LogoUrl) {
      const img = document.createElement('img');
      img.src = channel.LogoUrl;
      img.alt = '';
      img.loading = 'lazy';
      img.classList.add('managed-channel-logo');
      td.appendChild(img);
    }
    tr.appendChild(td);

    // --- Name (+ number + catch-up badge) ---
    td = document.createElement('td');
    const name = document.createElement('div');
    name.innerText = channel.Name;
    td.appendChild(name);

    const meta = document.createElement('small');
    meta.classList.add('managed-channel-meta');
    meta.innerText = `#${channel.Number}`;
    if (channel.HasCatchup) {
      const badge = document.createElement('span');
      badge.classList.add('managed-channel-badge');
      badge.title = `Catch-up available for ${channel.CatchupDuration} days.`;
      badge.innerText = `catch-up ${channel.CatchupDuration}d`;
      meta.appendChild(document.createTextNode(' '));
      meta.appendChild(badge);
    }
    td.appendChild(meta);
    tr.appendChild(td);

    // --- Category ---
    td = document.createElement('td');
    const category = document.createElement('input');
    category.type = 'text';
    category.setAttribute('is', 'emby-input');
    category.setAttribute('list', 'Categories');
    category.placeholder = channel.SuggestedCategory || 'Uncategorized';
    category.value = overrides.Category ?? '';
    category.onchange = () => category.value ?
      overrides.Category = category.value :
      delete overrides.Category;
    td.appendChild(category);
    tr.appendChild(td);

    // --- EPG source ---
    td = document.createElement('td');
    const epgSource = document.createElement('input');
    epgSource.type = 'text';
    epgSource.setAttribute('is', 'emby-input');
    epgSource.setAttribute('list', 'EpgSources');
    epgSource.placeholder = `${channel.Id} (self)`;
    epgSource.value = overrides.EpgStreamId ?? '';
    const effectiveEpgId = () => {
      const v = parseInt(epgSource.value, 10);
      return Number.isInteger(v) ? v : channel.Id;
    };
    epgSource.onchange = () => {
      const v = parseInt(epgSource.value, 10);
      if (Number.isInteger(v) && v !== channel.Id) {
        overrides.EpgStreamId = v;
      } else {
        delete overrides.EpgStreamId;
        epgSource.value = '';
      }
    };
    td.appendChild(epgSource);
    tr.appendChild(td);

    // --- Guide preview ---
    td = document.createElement('td');
    const check = document.createElement('button');
    check.type = 'button';
    check.setAttribute('is', 'emby-button');
    check.classList.add('raised', 'emby-button');
    check.innerText = 'Check guide';
    const guide = document.createElement('div');
    guide.classList.add('managed-channel-guide');
    check.onclick = () => {
      guide.innerHTML = '';
      const loading = document.createElement('small');
      loading.innerText = 'Loading…';
      guide.appendChild(loading);
      Xtream.fetchJson(`Xtream/Epg/${effectiveEpgId()}?limit=4`).then((entries) => {
        guide.innerHTML = '';
        if (!entries || entries.length === 0) {
          const empty = document.createElement('small');
          empty.classList.add('managed-channel-guide-empty');
          empty.innerText = 'No EPG data for this source.';
          guide.appendChild(empty);
          return;
        }
        for (const entry of entries) {
          const line = document.createElement('small');
          line.classList.add('managed-channel-guide-line');
          if (entry.NowPlaying) {
            line.classList.add('managed-channel-guide-now');
          }
          line.innerText = (entry.NowPlaying ? '▶ ' : '') + formatProgram(entry);
          guide.appendChild(line);
        }
      }).catch(() => {
        guide.innerHTML = '';
        const err = document.createElement('small');
        err.classList.add('managed-channel-guide-empty');
        err.innerText = 'Failed to load EPG.';
        guide.appendChild(err);
      });
    };
    td.appendChild(check);
    td.appendChild(guide);
    tr.appendChild(td);

    // --- Disable ---
    td = document.createElement('td');
    const disable = document.createElement('button');
    disable.type = 'button';
    disable.setAttribute('is', 'emby-button');
    disable.classList.add('raised', 'emby-button', 'button-cancel');
    disable.innerText = 'Disable';
    disable.onclick = () => {
      if (!window.confirm(`Disable "${channel.Name}"? It will be removed from your Live TV selection.`)) {
        return;
      }
      Dashboard.showLoadingMsg();
      ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl(`Xtream/LiveTv/${channel.Id}/Disable`),
      }).then(() => {
        delete overrides.Category;
        delete overrides.EpgStreamId;
        tr.remove();
        Dashboard.hideLoadingMsg();
      }).catch(() => {
        Dashboard.hideLoadingMsg();
        Dashboard.alert('Failed to disable the channel.');
      });
    };
    td.appendChild(disable);
    tr.appendChild(td);

    return tr;
  };

  const populateCategoriesDatalist = (datalist, channels, data) => {
    const categories = new Set([CATCHUP_CATEGORY]);
    for (const channel of channels) {
      if (channel.SuggestedCategory) {
        categories.add(channel.SuggestedCategory);
      }
    }
    for (const key of Object.keys(data)) {
      if (data[key]?.Category) {
        categories.add(data[key].Category);
      }
    }
    for (const category of [...categories].sort((a, b) => a.localeCompare(b))) {
      const option = document.createElement('option');
      option.value = category;
      datalist.appendChild(option);
    }
  };

  const populateEpgSourcesDatalist = (datalist, channels) => {
    for (const channel of channels) {
      const option = document.createElement('option');
      option.value = channel.Id;
      option.label = `${channel.Name} (#${channel.Number})`;
      datalist.appendChild(option);
    }
  };

  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(3);

    const getConfig = ApiClient.getPluginConfiguration(pluginId);
    const table = view.querySelector('#ManagedChannels');
    const filter = view.querySelector('#ChannelFilter');
    Dashboard.showLoadingMsg();

    Promise.all([
      getConfig.then((config) => config.LiveTvOverrides),
      Xtream.fetchJson('Xtream/LiveTv'),
    ]).then(([data, channels]) => {
      populateCategoriesDatalist(view.querySelector('#Categories'), channels, data);
      populateEpgSourcesDatalist(view.querySelector('#EpgSources'), channels);

      for (const channel of channels) {
        data[channel.Id] ??= {};
        const row = createChannelRow(Xtream, channel, data[channel.Id], channels);
        table.appendChild(row);
      }
      Dashboard.hideLoadingMsg();

      filter.addEventListener('input', () => {
        const needle = filter.value.trim().toLowerCase();
        for (const row of table.querySelectorAll('tr')) {
          const haystack = row.innerText.toLowerCase();
          row.style.display = !needle || haystack.includes(needle) ? '' : 'none';
        }
      });

      view.querySelector('#XtreamManagedChannelsForm').addEventListener('submit', (e) => {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then((config) => {
          config.LiveTvOverrides = Xtream.filter(
            data,
            overrides => Object.keys(overrides).length > 0
          );
          ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
            Dashboard.processPluginConfigurationUpdateResult(result);
          });
        });

        e.preventDefault();
        return false;
      });
    }).catch((error) => {
      console.error('Failed to load managed channels:', error);
      Dashboard.hideLoadingMsg();
      table.innerHTML = '';
      const errorRow = document.createElement('tr');
      const errorCell = document.createElement('td');
      errorCell.colSpan = 6;
      errorCell.style.color = '#ff6b6b';
      errorCell.style.padding = '16px';
      errorCell.innerHTML = 'Failed to load channels. Please check:<br>' +
        '1. Xtream credentials are configured (Credentials tab)<br>' +
        '2. Channels are selected (Live TV tab)<br>' +
        '3. Browser console for detailed errors';
      errorRow.appendChild(errorCell);
      table.appendChild(errorRow);
    });
  }));
}
