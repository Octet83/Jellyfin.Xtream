export default function (view) {
  view.addEventListener("viewshow", () => import(
    ApiClient.getUrl("web/ConfigurationPage", {
      name: "Xtream.js",
    })
  ).then((Xtream) => Xtream.default
  ).then((Xtream) => {
    const pluginId = Xtream.pluginConfig.UniqueId;
    Xtream.setTabs(1);

    const visible = view.querySelector("#Visible");
    const timezone = view.querySelector("#CatchupTimeZone");
    const liveCaledonian = view.querySelector("#LiveAtCaledonianTime");
    const startupDelay = view.querySelector("#LiveStartupDelay");
    const mergeQualities = view.querySelector("#MergeQualityVariants");
    const language = view.querySelector("#LiveLanguage");
    const search = view.querySelector("#LiveSearch");
    const onDemand = view.querySelector("#LiveOnDemand");
    const summary = view.querySelector("#LiveGroupsSummary");
    const list = view.querySelector("#LiveGroups");

    // The channels of the listed language and the keys of the ticked ones. The
    // selection lives here, not in the DOM: filtered-out rows keep their state.
    let groups = [];
    let loaded = false;
    const selected = new Set();

    const badge = (text, kind) => {
      const span = document.createElement("span");
      span.className = "live-group-badge" + (kind ? " live-group-badge-" + kind : "");
      span.textContent = text;
      return span;
    };

    const updateSummary = () => {
      const tv = groups.filter((g) => !g.OnDemand).length;
      summary.textContent = loaded
        ? `${selected.size} channel(s) selected · ${tv} TV channels listed for this language`
        : "";
    };

    const render = () => {
      const query = search.value.trim().toLowerCase();
      list.innerHTML = "";
      const byCategory = new Map();
      for (const group of groups) {
        if (group.OnDemand && !onDemand.checked) continue;
        if (query && !group.Name.toLowerCase().includes(query)) continue;
        if (!byCategory.has(group.Category)) byCategory.set(group.Category, []);
        byCategory.get(group.Category).push(group);
      }
      if (loaded && byCategory.size === 0) {
        list.textContent = "No channel matches.";
      }
      for (const [category, items] of byCategory) {
        const section = document.createElement("div");
        section.className = "live-group-section";

        const header = document.createElement("div");
        header.className = "live-group-header";
        const title = document.createElement("h3");
        title.textContent = `${category} (${items.length})`;
        header.appendChild(title);
        for (const [label, value] of [["All", true], ["None", false]]) {
          const button = document.createElement("button");
          button.type = "button";
          button.setAttribute("is", "emby-button");
          button.className = "emby-button live-group-bulk";
          button.textContent = label;
          button.addEventListener("click", () => {
            for (const item of items) {
              if (value) selected.add(item.Key); else selected.delete(item.Key);
            }
            render();
          });
          header.appendChild(button);
        }
        section.appendChild(header);

        for (const item of items) {
          const row = document.createElement("label");
          row.className = "live-group-row";
          const checkbox = document.createElement("input");
          checkbox.type = "checkbox";
          checkbox.checked = selected.has(item.Key);
          checkbox.addEventListener("change", () => {
            if (checkbox.checked) selected.add(item.Key); else selected.delete(item.Key);
            updateSummary();
          });
          row.appendChild(checkbox);
          const name = document.createElement("span");
          name.className = "live-group-name";
          name.textContent = item.Name;
          row.appendChild(name);
          for (const quality of item.Qualities) row.appendChild(badge(quality));
          if (item.HasCatchup) row.appendChild(badge("catch-up", "catchup"));
          section.appendChild(row);
        }
        list.appendChild(section);
      }
      updateSummary();
    };

    const load = () => {
      const lang = (language.value.trim() || "FR").toUpperCase();
      loaded = false;
      groups = [];
      selected.clear();
      list.textContent = "Loading the provider catalogue…";
      updateSummary();
      return Xtream.fetchJson(`Xtream/LiveGroups?language=${encodeURIComponent(lang)}`).then((data) => {
        groups = data;
        for (const group of groups) {
          if (group.Selected) selected.add(group.Key);
        }
        loaded = true;
        render();
      }).catch((error) => {
        console.error("Failed to load the Live TV channels:", error);
        list.textContent = "Failed to load the channels. Check the Xtream credentials (Credentials tab) and that the provider is reachable.";
      });
    };

    search.addEventListener("input", render);
    onDemand.addEventListener("change", render);
    language.addEventListener("change", load);

    ApiClient.getPluginConfiguration(pluginId).then((config) => {
      visible.checked = config.IsCatchupVisible;
      timezone.value = config.CatchupTimeZoneId ?? "";
      liveCaledonian.checked = config.LiveAtCaledonianTime;
      startupDelay.value = config.LiveStartupDelaySeconds ?? 12;
      mergeQualities.checked = config.MergeQualityVariants ?? true;
      language.value = config.LiveLanguage || "FR";
      return load();
    });

    view.querySelector("#XtreamLiveForm").addEventListener("submit", (e) => {
      e.preventDefault();
      Dashboard.showLoadingMsg();
      const lang = (language.value.trim() || "FR").toUpperCase();

      // The settings first, then the selection: the selection endpoint edits and
      // saves the configuration server-side, which also refreshes the channels.
      ApiClient.getPluginConfiguration(pluginId).then((config) => {
        config.IsCatchupVisible = visible.checked;
        config.CatchupTimeZoneId = timezone.value.trim() || "Pacific/Noumea";
        config.LiveAtCaledonianTime = liveCaledonian.checked;
        config.LiveStartupDelaySeconds = Math.max(0, Math.min(60, parseInt(startupDelay.value, 10) || 0));
        config.MergeQualityVariants = mergeQualities.checked;
        config.LiveLanguage = lang;
        return ApiClient.updatePluginConfiguration(pluginId, config);
      }).then((result) => {
        if (!loaded) return result;
        return ApiClient.fetch({
          type: "POST",
          url: ApiClient.getUrl(`Xtream/LiveGroups?language=${encodeURIComponent(lang)}`),
          data: JSON.stringify([...selected]),
          contentType: "application/json",
        }).then(() => result);
      }).then((result) => {
        Dashboard.processPluginConfigurationUpdateResult(result);
      }).catch((error) => {
        console.error("Failed to save the Live TV settings:", error);
        Dashboard.hideLoadingMsg();
        Dashboard.alert("Failed to save the Live TV selection.");
      });
      return false;
    });
  }));
}
