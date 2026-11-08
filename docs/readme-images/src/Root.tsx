import { ControlsStill } from "./Controls";
import { HeroStill } from "./Hero";
import { HowToUseStill } from "./HowToUse";

// README images. Render all three with:  npm run render
export const RemotionRoot: React.FC = () => {
  return (
    <>
      <HeroStill />
      <HowToUseStill />
      <ControlsStill />
    </>
  );
};
