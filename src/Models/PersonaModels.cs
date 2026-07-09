using System.Collections.Generic;

namespace StudentAge.QQAIMoments.Models
{
    internal sealed class PersonaFileData
    {
        public int Version = 2;
        public Dictionary<string, NpcPersona> Personas = new Dictionary<string, NpcPersona>();
    }

    internal sealed class NpcPersona
    {
        public bool Enabled = true;
        public int RoleId;
        public int PresetVersion;
        public string Source = "";
        public string DisplayName = "";
        public string Persona = "";
        public string SpeakingStyle = "";
        public string RelationshipHint = "";
        public List<string> Backstory = new List<string>();
        public List<string> CoreTraits = new List<string>();
        public List<string> Values = new List<string>();
        public List<string> Boundaries = new List<string>();
        public List<string> BehaviorRules = new List<string>();
        public List<string> SpeechPatterns = new List<string>();
        public List<string> Catchphrases = new List<string>();
        public List<string> RelationshipRules = new List<string>();
        public List<string> EmotionalRules = new List<string>();
        public List<string> Mannerisms = new List<string>();
        public List<string> PostTopics = new List<string>();
        public List<string> Likes = new List<string>();
        public List<string> Dislikes = new List<string>();
        public List<string> ReplyStyleRules = new List<string>();
        public List<string> ThumbRules = new List<string>();
        public List<string> FavorLayers = new List<string>();
        public List<string> ConflictRules = new List<string>();
        public List<string> ActivePostTriggers = new List<string>();
        public List<string> SamplePosts = new List<string>();
        public List<string> SampleComments = new List<string>();
    }

    internal sealed class PersonaContext
    {
        internal int RoleId;
        internal string Name;
        internal string Persona;
        internal string SpeakingStyle;
        internal string RelationshipHint;
        internal string Introduction;
        internal string Profile;
        internal string Note;
        internal List<string> Backstory;
        internal List<string> CoreTraits;
        internal List<string> Values;
        internal List<string> Boundaries;
        internal List<string> BehaviorRules;
        internal List<string> SpeechPatterns;
        internal List<string> Catchphrases;
        internal List<string> RelationshipRules;
        internal List<string> EmotionalRules;
        internal List<string> Mannerisms;
        internal List<string> PostTopics;
        internal List<string> Likes;
        internal List<string> Dislikes;
        internal List<string> ReplyStyleRules;
        internal List<string> ThumbRules;
        internal List<string> FavorLayers;
        internal List<string> ConflictRules;
        internal List<string> ActivePostTriggers;
        internal List<string> SamplePosts;
        internal List<string> SampleComments;
        internal int Relation;
        internal float Favor;
        internal string Gender;
    }
}
