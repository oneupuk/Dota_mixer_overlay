import { AbsoluteFill, Img, Interactive, Still, staticFile } from "remotion";
import { inter, mono } from "./fonts";

// docs/how-to-use.png - the three steps, left to right.

type CardProps = {
  readonly step: number;
  readonly left: number;
  readonly title: string;
  readonly body: string;
  readonly children: React.ReactNode;
};

const Card: React.FC<CardProps> = ({ step, left, title, body, children }) => {
  return (
    <div
      style={{
        position: "absolute",
        left,
        top: 40,
        width: 480,
        height: 500,
        borderRadius: 24,
        overflow: "hidden",
        background: "linear-gradient(180deg, rgba(255,255,255,0.055), rgba(255,255,255,0.025))",
        border: "1px solid rgba(255,255,255,0.09)",
        boxShadow: "0 24px 60px rgba(0,0,0,0.45)",
      }}
    >
      <div
        style={{
          position: "absolute",
          left: 0,
          top: 0,
          width: 480,
          height: 290,
          overflow: "hidden",
          background: "#07090f",
          borderBottom: "1px solid rgba(255,255,255,0.08)",
        }}
      >
        {children}
      </div>
      <div style={{ position: "absolute", left: 32, top: 318, right: 32 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 14 }}>
          <div
            style={{
              width: 38,
              height: 38,
              borderRadius: 19,
              background: "linear-gradient(135deg, #ffb057, #e2483a)",
              color: "white",
              fontSize: 20,
              fontWeight: 800,
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              boxShadow: "0 6px 18px rgba(226,72,58,0.45)",
            }}
          >
            {step}
          </div>
          <div style={{ color: "#f4f6fb", fontSize: 30, fontWeight: 700, letterSpacing: -0.4 }}>{title}</div>
        </div>
        <div style={{ marginTop: 18, color: "#a9b1c4", fontSize: 21, lineHeight: 1.5 }}>{body}</div>
      </div>
    </div>
  );
};

const Cursor: React.FC<{ readonly x: number; readonly y: number }> = ({ x, y }) => {
  return (
    <svg
      width={34}
      height={46}
      viewBox="0 0 17 23"
      style={{ position: "absolute", left: x, top: y, filter: "drop-shadow(0 3px 5px rgba(0,0,0,0.6))" }}
    >
      <path d="M1 1 L1 18 L5.2 14.2 L8.3 21.3 L11.2 20 L8.2 13 L14 13 Z" fill="white" stroke="black" strokeWidth={1.2} strokeLinejoin="round" />
    </svg>
  );
};

export const HowToUse: React.FC = () => {
  return (
    <AbsoluteFill
      name="Background"
      style={{
        background:
          "radial-gradient(ellipse 60% 70% at 50% 0%, rgba(255,140,70,0.12), rgba(0,0,0,0) 70%), linear-gradient(180deg, #121624 0%, #0a0c14 100%)",
        fontFamily: inter,
      }}
    >
      <Card
        step={1}
        left={48}
        title="Launch Dota from Steam"
        body="Add the launch option once. The overlay then starts with Dota and quits with it."
      >
        <Interactive.Div
          name="Steam launch options mock"
          style={{
            position: "absolute",
            left: 28,
            top: 34,
            width: 424,
            color: "#c7d0de",
            fontSize: 15,
          }}
        >
          <div style={{ color: "#7f8aa0", fontSize: 14, fontWeight: 600, letterSpacing: 1.2 }}>
            DOTA 2 › PROPERTIES › GENERAL
          </div>
          <div style={{ marginTop: 22, color: "#e8ecf3", fontSize: 15, fontWeight: 700, letterSpacing: 1.4 }}>
            LAUNCH OPTIONS
          </div>
          <div
            style={{
              marginTop: 12,
              padding: "14px 16px",
              borderRadius: 8,
              background: "#11151c",
              border: "1px solid #3a4352",
              boxShadow: "inset 0 1px 3px rgba(0,0,0,0.5), 0 0 0 3px rgba(255,160,90,0.18)",
              fontFamily: mono,
              fontSize: 16,
              lineHeight: 1.55,
              color: "#e9edf5",
            }}
          >
            <div style={{ color: "#ffc27a" }}>"D:\…\bin\DotaGlassOverlay.exe"</div>
            <div>
              %command% <span style={{ color: "#8fd1ff" }}>-gamestateintegration</span>
            </div>
          </div>
          <div
            style={{
              marginTop: 18,
              display: "inline-block",
              padding: "8px 14px",
              borderRadius: 999,
              background: "rgba(255,255,255,0.07)",
              border: "1px solid rgba(255,255,255,0.12)",
              color: "#c9d1df",
              fontSize: 14,
            }}
          >
            Tray icon › <b style={{ color: "#fff" }}>Copy Steam launch option</b>
          </div>
        </Interactive.Div>
      </Card>

      <Card
        step={2}
        left={560}
        title="Click the sun/moon"
        body="In a match, the clock icon becomes a button. It glows when you hover it."
      >
        <Img
          name="Clock zoom"
          src={staticFile("clock-hover.png")}
          style={{ position: "absolute", left: -250, top: -4, width: 980, height: 294 }}
        />
        <Cursor x={262} y={58} />
      </Card>

      <Card
        step={3}
        left={1072}
        title="Control your music"
        body="Skip, pause, seek or set the volume. It folds away after 5 s idle, or on another click."
      >
        <Img
          name="Widget in game"
          src={staticFile("game-open.jpg")}
          style={{ position: "absolute", left: -300, top: 0, width: 1080, height: 607.5 }}
        />
      </Card>
    </AbsoluteFill>
  );
};

export const HowToUseStill: React.FC = () => {
  return <Still id="HowToUse" component={HowToUse} width={1600} height={580} />;
};
